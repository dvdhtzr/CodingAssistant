# Order pricing — realistic C# repair challenge

This is a self-contained .NET 10 application with several collaborating classes:
`OrderRequest`, `OrderLine`, `PricingCalculator`, `OrderService`, and
`InMemoryOrderRepository`. It has no third-party dependencies or cloud access.

**The initial implementation has a deliberate business-logic defect:**
a SAVE10 coupon reduces the price of taxable merchandise, but tax is incorrectly
computed from the pre-discount taxable subtotal. The source compiles successfully,
so a build-only check cannot discover the problem.

The model is permitted to replace **only `PricingCalculator.cs`**. The independent
acceptance harness in `Program.cs` exercises mixed taxable/exempt merchandise,
cent rounding, tax exclusions, coupon and input validation, and repository
idempotence. A passing result must include
`ORDER-PRICING ACCEPTANCE PASS` and `10/10 checks passed`.

Run the CodingAssistant CLI from the repository root on the Dell:

```bash
python3 tools/mvp/assist.py --project benchmarks/fixtures/04-order-pricing --file PricingCalculator.cs --task "Fix the incorrect tax calculation for SAVE10. Apply the coupon to all merchandise but compute tax only on taxable merchandise after its proportional discount. Keep two-decimal rounding away from zero, do not discount or tax shipping, and preserve input validation and all public signatures. The executable acceptance checks must pass." --run
```

Every generated diff requires explicit `APPLY`, and all source changes and
execution occur in an approved disposable copy under `artifacts/mvp/`.
The original fixture and acceptance tests remain unchanged. `--run` performs
fixed restore/build/run commands in the existing offline, rootless,
network-denied Podman container. A test failure does **not** automatically
authorize an AI retry; automatic retries remain limited to compiler diagnostics.

The frozen fixture should fail acceptance before repair and pass with the
known-good discounted-tax formula. GitHub CI checks both cases using a static
known-good patch, not a model-generated edit. This does not count as an M0
model qualification run.
