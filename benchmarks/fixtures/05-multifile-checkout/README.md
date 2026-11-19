# Multi-file checkout contract — CodingAssistant challenge

This .NET 10 app demonstrates a **real two-file change** on a small,
multi-class project. The owner chooses exactly two existing files to edit:
`DiscountPolicy.cs` and `CheckoutCalculator.cs`. The remaining domain
types are read-only reference context. `Program.cs` is an independent
executable acceptance oracle, never passed to Ollama as nearby context.

The two intentional defects are:
1. `DiscountPolicy.Rate` does not recognize `MEMBER20` for eligible members.
2. `CheckoutCalculator.Quote` calculates tax before applying the coupon's
   proportional discount to taxable merchandise.

The benchmark requires a merchandise-wide coupon rounded **once at order
level** and correct shipping exclusions, tax, validation and member eligibility.

On the Dell, after the feature PR is merged:

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/mvp/assist.py --project benchmarks/fixtures/05-multifile-checkout --files DiscountPolicy.cs CheckoutCalculator.cs --task "Support MEMBER20 only for members, retain SAVE10 for everyone, apply all coupons once to total merchandise and round the order-wide discount once, then tax only taxable merchandise after its proportional discount. Preserve validation, shipping exclusions and public signatures. Repair both selected classes." --model qwen3.5:4b --inference-timeout 900 --run
```

The model proposes one complete replacement **per selected file**, with bounded
C# neighbor context. You inspect the combined unified diff and approve both
changes together by typing `APPLY`. A failed model response, timeout or user
rejection **cannot partially apply** these files. Originals remain unchanged.
The selected project is copied to `artifacts/mvp/<run>/project`; the offline
rootless Podman toolchain builds and executes the copy when `--run` is set.

Expected acceptance: `MULTI-FILE CHECKOUT: 9/9 checks passed` plus
`MULTI-FILE CHECKOUT PASS`. Compiler/test failures can yield two additional
reviewed, explicit-approval proposals, but infrastructure errors do not.

The deterministic CI contract applies a known-good fix in a disposable copy.
Passing model-free CI does not qualify any Ollama model or establish the Dell
can solve the challenge; its real outcome must be observed.
