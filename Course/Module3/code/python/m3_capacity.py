"""What does one investigation actually cost in tokens?

DEMONSTRATION CODE - written to be read rather than deployed.

A tool-calling agent makes many more model calls than a single-shot assessment does, so its cost is
worth measuring rather than assuming. This runs one real investigation, as far as the human gate,
with a meter attached to both agents, and prints what crossed the wire. It writes no referral, and
it replaces any investigation that is waiting for a decision.

    python m3_capacity.py
"""
import asyncio

from agent_framework import ChatMiddleware

import m3_frauddesk as fd
from m3_types import SeedClaim

SEED = "BCL-2026-0201"


class Meter(ChatMiddleware):
    """Chat middleware: counts the tokens of every model call, after the call returns.

    Middleware sees each call on its way through without the desk knowing it is there, so
    measuring cannot change what the desk does.
    """

    def __init__(self):
        self.calls = []

    async def process(self, context, call_next) -> None:
        await call_next()
        usage = getattr(getattr(context, "result", None), "usage_details", None) or {}
        if usage:
            self.calls.append({"input": usage.get("input_token_count") or 0,
                               "output": usage.get("output_token_count") or 0,
                               "total": usage.get("total_token_count") or 0})
        else:
            self.calls.append({"input": 0, "output": 0, "total": 0, "unmeasured": True})


async def main():
    """Run ONE real investigation with the meter attached, then print what it cost."""
    meter = Meter()
    fd.EXTRA_MIDDLEWARE.append(meter)
    for p in fd.CHECKPOINT_DIR.glob("*.json"):
        p.unlink()
    wf, _ = fd.build_workflow()
    await wf.run(SeedClaim(claim_ref=SEED))

    measured = [c for c in meter.calls if not c.get("unmeasured")]
    total_in = sum(c["input"] for c in measured)
    total_out = sum(c["output"] for c in measured)
    total = sum(c["total"] for c in measured) or (total_in + total_out)

    print("\n" + "=" * 74)
    print("  ONE INVESTIGATION")
    print("=" * 74)
    print(f"  model calls        : {len(meter.calls)}  ({len(measured)} measured)")
    print(f"  tool calls         : {len(fd.TOOL_LOG)}")
    print(f"  input tokens       : {total_in:,}")
    print(f"  output tokens      : {total_out:,}")
    print(f"  total tokens       : {total:,}")
    if not measured:
        print("  (this framework version did not report usage for these calls)")


if __name__ == "__main__":
    asyncio.run(main())
