#!lua name=driverprojection
-- B-W04-04: single atomic projection function (05B section 5, 01 section 21.2).
-- One invocation applies AT MOST one state section and/or one location section.
-- Empty-string arg = section absent (caller sends state-only or location-only updates
-- via separate DTOs; a state event can never smuggle a stale location).
--
-- Frozen W4 policies:
--   state:    apply iff version > projected version (DSI-007). No current -> accept.
--   location: no current -> accept; same session -> seq-first, capturedAt tie-break
--             (DSI-006); DIFFERENT session -> 'stale-session' reject. Redis cannot
--             order opaque sessionIds, and Docs 00-04 define no session-transition
--             authority, so W4 never auto-accepts a foreign session. Legitimate
--             session rotation needs a future Driver-owned current-session signal.
--   Known cost of that conservatism: a foreign write to locationSessionId
--             itself never heals (sessions are opaque; no rule can tell garbage
--             from rotation), bricking freshness for that driver until the key
--             is rebuilt. GEO still follows stored coords, so impact is
--             freshness-only, never authority.
--   geo:      recomputed from CURRENT hash values every call:
--             AVAILABLE + indexable coords -> GEOADD, else ZREM.
-- capturedAt must be ISO-8601 UTC so lexicographic compare == time order.
-- Returns: {state, location, geo} e.g. {'applied','skipped','added'}.
--
-- Corrupt-hash rule: the script itself never writes partial hashes, but a hash
-- touched by a foreign writer may hold missing/non-numeric ordering fields.
-- Guarded comparisons: an unparseable STORED ordering value heals (the valid
-- new sample overwrites garbage, which carries no ordering information), while
-- an unparseable CALLER value fails closed as 'stale'. The geo section never
-- errors on missing/non-numeric coordinates: unknown coords mean ZREM.
--
-- Key-type gate: a wrong-typed hash or GEO key (a foreign writer repurposing
-- Matching-owned keys) fails the whole call with 'keytype-error' BEFORE any
-- write, so a type violation can never leave a partial mutation behind.
-- Callers map the unknown token to Degraded. Key-type ownership, not value
-- garbage, is the boundary: writers must never repurpose Matching-owned keys.

local function keytype_ok(key, want)
  local t = redis.call('TYPE', key)
  if t.ok == 'none' then
    return true
  end
  for _, v in ipairs(want) do
    if t.ok == v then
      return true
    end
  end
  return false
end

-- ISO-8601 UTC with mandatory Z suffix and sane ranges. Our sole writer
-- emits UtcDateTime "O" (fixed-width, Z-suffixed); lexicographic compare is a
-- time order ONLY within this shape. Digit-shaped garbage ("2026-99-99T99:x")
-- or offset suffixes ("+02:00") fail validation: stored garbage carries no
-- ordering information (heal), caller garbage fails closed (stale).
local function isTs(v)
  if type(v) ~= 'string' then
    return false
  end
  local y, mo, d, h, mi, s = v:match('^(%d%d%d%d)%-(%d%d)%-(%d%d)T(%d%d):(%d%d):(%d%d)%.?%d*Z$')
  if not y then
    return false
  end
  mo, d, h, mi, s = tonumber(mo), tonumber(d), tonumber(h), tonumber(mi), tonumber(s)
  return mo >= 1 and mo <= 12 and d >= 1 and d <= 31
    and h >= 0 and h <= 23 and mi >= 0 and mi <= 59 and s >= 0 and s <= 59
end

redis.register_function('driver_projection_update', function(keys, args)
  local hash = keys[1]
  local geo = keys[2]
  local member = args[1]
  -- state section
  local status = args[2]
  local version = args[3]
  -- location section
  local lon = args[4]
  local lat = args[5]
  local session = args[6]
  local seq = args[7]
  local capturedAt = args[8]

  if (not keytype_ok(hash, {'hash'})) or (not keytype_ok(geo, {'zset'})) then
    return {'keytype-error', 'keytype-error', 'keytype-error'}
  end

  local state = 'skipped'
  if version and version ~= '' then
    local newV = tonumber(version)
    if not newV then
      state = 'stale'
    else
      local cur = redis.call('HGET', hash, 'driverAggregateVersion')
      if cur == false then
        redis.call('HSET', hash,
          'derivedStatus', status,
          'driverAggregateVersion', version)
        state = 'applied'
      else
        local curV = tonumber(cur)
        if (not curV) or (newV > curV) then
          redis.call('HSET', hash,
            'derivedStatus', status,
            'driverAggregateVersion', version)
          state = 'applied'
        else
          state = 'stale'
        end
      end
    end
  end

  local loc = 'skipped'
  if seq and seq ~= '' then
    local s = tonumber(seq)
    local nlon = tonumber(lon)
    local nlat = tonumber(lat)
    if (not s) or (not nlon) or (not nlat)
      or (not session) or (session == '')
      or (not isTs(capturedAt)) then
      loc = 'stale'
    else
      local curSession = redis.call('HGET', hash, 'locationSessionId')
      if curSession == false then
        redis.call('HSET', hash,
          'longitude', lon,
          'latitude', lat,
          'locationSessionId', session,
          'sequenceNo', seq,
          'capturedAt', capturedAt)
        loc = 'applied'
      elseif session == curSession then
        local curSeq = tonumber(redis.call('HGET', hash, 'sequenceNo'))
        local curTs = redis.call('HGET', hash, 'capturedAt')
        if (not curSeq) or (not isTs(curTs)) then
          redis.call('HSET', hash,
            'longitude', lon,
            'latitude', lat,
            'locationSessionId', session,
            'sequenceNo', seq,
            'capturedAt', capturedAt)
          loc = 'applied'
        elseif (s > curSeq) or (s == curSeq and capturedAt > curTs) then
          redis.call('HSET', hash,
            'longitude', lon,
            'latitude', lat,
            'locationSessionId', session,
            'sequenceNo', seq,
            'capturedAt', capturedAt)
          loc = 'applied'
        else
          loc = 'stale'
        end
      else
        loc = 'stale-session'
      end
    end
  end

  local st = redis.call('HGET', hash, 'derivedStatus')
  local la = tonumber(redis.call('HGET', hash, 'latitude'))
  local lo = tonumber(redis.call('HGET', hash, 'longitude'))
  local geoRes = 'removed'
  if st == 'AVAILABLE'
    and la and lo
    and la >= -85.05112878 and la <= 85.05112878
    and lo >= -180 and lo <= 180 then
    redis.call('GEOADD', geo, tostring(lo), tostring(la), member)
    geoRes = 'added'
  else
    redis.call('ZREM', geo, member)
  end

  return {state, loc, geoRes}
end)
