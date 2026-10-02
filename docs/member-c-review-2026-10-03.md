# Review Member C Distributed W1–W4 — 2026-10-03

## Phạm vi và kết luận

Đối chiếu code và lịch sử branch với Docs 00–04 và issue pack 05C §20–23 của repo Distributed. Người dùng đã xác nhận chỉ review Distributed. Link chat Mobile dùng bộ docs và phân công khác nên không được dùng để giao Identity cho C trong repo này.

**Chưa đủ căn cứ nghiệm thu trọn W1–W4.** CI thành công chứng minh các kiểm tra hiện có đã qua; không chứng minh mọi đầu việc trong phân công đã hoàn thành. Đây là review tĩnh và kiểm tra trạng thái GitHub; không chạy lại test hoặc Docker trong lần review này.

## Findings cần xử lý

### F1 — P2 — W4 thiếu security/auth negative test skeleton

- Yêu cầu: Doc 04 §15, dòng 1225. Doc 03 SEC-01..04 mô tả token thiếu/hỏng/hết hạn, sai role, truy cập object của người khác, giả actor ID.
- Hiện trạng: `tests/Integration/RideHailing.IdentityService.IntegrationTests/BootstrapTests.cs` chỉ có `Assert.True(true)`. Không có fixture/case tương ứng trên branch W4. `docs/member-c-week4.md` dòng 45 đẩy toàn bộ việc này sang sau khi có Identity.
- Hậu quả: Production simulator trả 404 và kiểm tra DB role isolation không kiểm tra các ranh giới auth này. Không thể đánh dấu security/auth skeleton hoàn thành.
- Hướng sửa: chuẩn bị test matrix, fixture/handoff contract và case không gây business mutation; ghi rõ những case còn blocked bởi API/auth của A. Chỉ chuyển case sang pass khi chạy được với implementation thật. C không tự thay thế owner Identity.

### F2 — P2 — W2 chưa có versioned Rabbit declaration skeleton

- Yêu cầu: C-W02-03, Doc 05C dòng 1447–1449.
- Kiểm tra chính branch `MemC-W2-Distributed`: `deploy/rabbitmq/README.md` chỉ là bootstrap placeholder, còn thư viện Messaging chỉ có `BootstrapMarker.cs`; chưa có declaration executable hoặc versioned definition.
- README W2 còn ghi exchanges/queues ở W8+, trái với việc W4 phải khai báo topology. `docs/member-c-week2.md` dòng 9 dùng thư mục bootstrap làm bằng chứng cho versioned declarations là chưa đủ.
- W4 đã có topology code nhưng điều đó không làm checkout W2 có đủ đầu việc W2.
- Hướng sửa: bổ sung bootstrap declaration tối thiểu vào W2, làm rõ phần mở rộng ở W4 và cập nhật bằng chứng theo từng branch.

### F3 — P2 — Aggregate Payment cho phép bỏ qua transition guards

- Vị trí: `src/Services/Payment/RideHailing.PaymentService/Domain/PaymentOperation.cs`, dòng 30–44.
- Positional public record sinh public constructor và các thuộc tính init công khai. Code gọi có thể viết `succeeded with { State = PaymentOperationState.Pending }`, sau đó gọi `BeginAttempt`, hoặc dựng trực tiếp một trạng thái không hợp lệ. `SaveAsync` không kiểm tra transition trước khi lưu.
- Các test chỉ kiểm tra `BeginAttempt`/`Resolve` từ một state nhất định, chưa bảo đảm aggregate chỉ thay đổi qua các phương thức hợp lệ.
- Hướng sửa: encapsulate construction/state mutation, có đường rehydrate được kiểm tra riêng và test bảo vệ các invariant. Đây là lỗ hổng API domain; review chưa tìm thấy endpoint hiện tại cho client trực tiếp truyền state để khai thác.

## Giới hạn simulator cần diễn đạt đúng

`PaymentSimulator.ExecuteAsync` chỉ delay với SlowSuccess; hai timeout mode gọi Resolve ngay. Endpoint trả 504 cùng object có `ProviderEffectOccurred`/`ProviderEffectId`. Hiệu ứng và kết quả nằm chung bản ghi operation; chưa có provider boundary độc lập ghi hiệu ứng rồi làm mất phản hồi.

Do đó bằng chứng W3 hiện tại là sáu kết quả xác định, state/effect flags và persistence. Không phải bằng chứng crash window hoặc timeout thực giữa Payment và provider như mô tả Doc 05C §5.2–5.3. Không đánh đồng giới hạn này với yêu cầu phải hoàn tất W7 reconciliation hoặc W11 fault injection ngay trong W3. Khi triển khai các tuần đó cần mở rộng simulator và bằng chứng.

## Đối chiếu issue pack

“Có” dưới đây nghĩa là tìm thấy artifact/code phù hợp, chưa thay cho peer acceptance.

| Issue | Trạng thái / bằng chứng |
|---|---|
| W1-01 | Có bảng bốn crash windows; chưa có bốn sơ đồ riêng như cách diễn đạt “Draw” trong issue |
| W1-02 | Có identity table |
| W1-03 | Có FSM dạng text |
| W1-04 | Có sơ đồ publish/confirm/delivery/ACK |
| W1-05 | Có retry taxonomy |
| W1-06 | Có evidence table/PR; ghi chú repo xác nhận chưa tạo Project board/fields |
| W1-07 | Chưa có bằng chứng team walkthrough |
| W1-08 | Có defense note; chưa có bằng chứng trình bày |
| W2-01 | Có core stack mặc định; không khai báo profile tên core, obs có profile riêng. Cách làm được giải thích trong ghi chú |
| W2-02 | Có DB roles và 30 negative CONNECT checks với positive controls |
| W2-03 | Thiếu declaration skeleton ở W2 — F2 |
| W2-04 | Có Collector baseline |
| W2-05 | Có Jaeger/Prometheus/Grafana; smoke kiểm tra trace, chưa chứng minh đầy đủ dashboard/metrics qua assertion riêng |
| W2-06 | Có HTTP liveness/readiness, readiness kiểm tra owned DB; worker là skeleton không có HTTP probe |
| W2-07 | Có service/instance identity trong telemetry |
| W2-08 | Có CI platform smoke |
| W2-09 | Có reset script, DB init; ghi chú chưa có business seed. Cần ghi rõ seed entry point/convention khi xuất hiện dữ liệu nghiệp vụ |
| W2-10 | CI clean checkout có; teammate clean-clone rerun chưa được xác nhận |
| W3-01 | Có aggregate nhưng mutation có thể bypass — F3 |
| W3-02 | Có Authorize/Capture/Void |
| W3-03 | Có FSM guards trên methods; giới hạn F3 |
| W3-04 | Có sáu deterministic modes; giới hạn timeout nêu trên |
| W3-05 | Có SQL schema/init và repository |
| W3-06 | Có legal/illegal method transition tests; chưa bảo vệ public mutation |
| W3-07 | Có state/effect assertions và DB smoke; không chứng minh provider boundary độc lập |
| W3-08 | A review Payment semantics chưa được xác nhận |
| W3-09 | Có CI/smoke evidence; chỉ dùng trong phạm vi assertion thực tế |
| W4-01 | Có generic canonical envelope |
| W4-02 | Có trace header helpers/tests |
| W4-03 | Có versioning convention ghi trong notes; A/B freeze/acceptance chưa xác nhận |
| W4-04 | Có hai durable topic exchanges |
| W4-05 | Có core queues/bindings và routing smoke |
| W4-06 | Có quorum/durable queues và persistent message properties; chưa phải HA proof |
| W4-07 | Có restart/redeclare smoke |
| W4-08 | Có secrets/config policy tối thiểu và Production simulator isolation |
| W4-09 | Có DB isolation smoke từ W2 |
| W4-10 | A/B contract review chưa được xác nhận |

Doc 04 bổ sung Notification worker skeleton ở W3: đã có. Doc 04 bổ sung security/auth negative test skeleton ở W4: còn thiếu, F1. Cross-review Redis envelope và trao đổi fixtures/chạy API của nhau cũng cần bằng chứng từ các thành viên.

## Trạng thái GitHub đọc ngày 2026-10-03

| Week | Head | PR | Trạng thái | Checks trên head |
|---|---|---|---|---|
| W1 | `1fd0e6d` | [#1](https://github.com/minhqng/ride-hailing-microservices-system/pull/1) | Closed, chưa merge | build-test và compose-config success |
| W2 | `d712367` | [#2](https://github.com/minhqng/ride-hailing-microservices-system/pull/2) | Open | build-test, compose-config, platform-smoke success |
| W3 | `e68f3b0` | [#3](https://github.com/minhqng/ride-hailing-microservices-system/pull/3) | Open | build-test, compose-config, platform-smoke success |
| W4 | `61ea04b` | [#4](https://github.com/minhqng/ride-hailing-microservices-system/pull/4) | Open | build-test, compose-config, platform-smoke success |

PRs được xếp main ← W1 ← W2 ← W3 ← W4. W1 đóng chưa merge nên chuỗi chưa được tích hợp vào main. Review này không tự mở lại, merge hoặc thay đổi PR.

## Thứ tự khắc phục đề xuất

1. W1: hoàn thiện artifact còn thiếu, cập nhật trạng thái review/acceptance thực tế và xử lý PR đã đóng theo quyết định của owner.
2. W2: bổ sung declaration skeleton, sửa ghi chú sai mốc tuần; kiểm tra lại W2 rồi cập nhật PR #2.
3. W3: đóng đường bypass aggregate, thêm kiểm tra có ý nghĩa; giữ claim simulator đúng phạm vi; cập nhật PR #3 sau khi nhận thay đổi W2.
4. W4: bổ sung auth negative test skeleton và handoff với A, rà contract với A/B; cập nhật PR #4 sau W3.

Không đưa các mục W5–W9 như operationKey dedup/fingerprint, concurrency, UNKNOWN reconciliation, confirms/manual ACK và Outbox vào danh sách thiếu của W1–W4. Chúng có mốc riêng trong playbook.
