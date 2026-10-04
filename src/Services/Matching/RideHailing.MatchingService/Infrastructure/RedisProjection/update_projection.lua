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
--   geo:      recomputed from CURRENT hash values every call:
--             AVAILABLE + indexable coords -> GEOADD, else ZREM.
-- capturedAt must be ISO-8601 UTC so lexicographic compare == time order.
-- Returns: {state, location, geo} e.g. {'applied','skipped','added'}.

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

  local state = 'skipped'
  if version and version ~= '' then
    local cur = redis.call('HGET', hash, 'driverAggregateVersion')
    if (not cur) or (tonumber(version) > tonumber(cur)) then
      redis.call('HSET', hash,
        'derivedStatus', status,
        'driverAggregateVersion', version)
      state = 'applied'
    else
      state = 'stale'
    end
  end

  local loc = 'skipped'
  if seq and seq ~= '' then
    local curSession = redis.call('HGET', hash, 'locationSessionId')
    if not curSession then
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
      local s = tonumber(seq)
      if (s > curSeq) or (s == curSeq and capturedAt > curTs) then
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

  local st = redis.call('HGET', hash, 'derivedStatus')
  local la = tonumber(redis.call('HGET', hash, 'latitude') or '99')
  local lo = tonumber(redis.call('HGET', hash, 'longitude') or '999')
  local geoRes = 'removed'
  if st == 'AVAILABLE'
    and la >= -85.05112878 and la <= 85.05112878
    and lo >= -180 and lo <= 180 then
    redis.call('GEOADD', geo, tostring(lo), tostring(la), member)
    geoRes = 'added'
  else
    redis.call('ZREM', geo, member)
  end

  return {state, loc, geoRes}
end)
