"""Assertions over the fraud desk. No model, no Dataverse, no cost, a few seconds.

DEMONSTRATION CODE - written to be read rather than deployed.

The point: the guarantees an insurer would ask about are checked in CODE, not argued for in a
document. "Nothing is referred without a named human" is a test that re-runs for free, not a
promise - and sampling model runs could never establish it.

    python m3_test.py          # exits non-zero if anything fails
"""
import asyncio
import dataclasses
import inspect
import re
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

from agent_framework import MiddlewareTermination
from agent_framework.exceptions import ToolException

import m3_dataverse as dv
import m3_frauddesk as fd
import m3_types as t

PASS, FAIL = [], []


def check(name, cond, detail=""):
    """Record one assertion and print it. Detail is shown only on failure, so a passing run reads
    as a list of the guarantees the desk makes rather than as debug output."""
    (PASS if cond else FAIL).append(name)
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail and not cond
                                                        else ""))


print("=== the graph ===")

# Delivery is by message type. If two executors accept the same type, BOTH receive the message,
# which produces no output and no error. The graph is only safe because the types are disjoint.
OUR_TYPES = {n for n in dir(t) if not n.startswith("_")}


def handler_types(cls):
    """The message types THIS class declares a handler for.

    `cls.__dict__` and not `getmembers`, because the latter also walks methods inherited from
    Executor and reports base-class types the graph never uses.
    """
    out = set()
    for fn in cls.__dict__.values():
        fn = getattr(fn, "__wrapped__", fn)
        if not callable(fn) or not hasattr(fn, "__annotations__"):
            continue
        try:
            params = list(inspect.signature(fn).parameters.values())
        except (TypeError, ValueError):
            continue
        for p in params[1:]:
            name = getattr(p.annotation, "__name__", str(p.annotation))
            if name in OUR_TYPES:
                out.add(name)
                break
    return out


types_by_node = {c.__name__: handler_types(c)
                 for c in (fd.Intake, fd.Investigate, fd.Challenge, fd.Refer)}
seen, overlap = set(), set()
for node, ts in types_by_node.items():
    overlap |= (seen & ts)
    seen |= ts
check("every executor accepts a distinct message type", not overlap, f"shared: {overlap}")

# Asserted from the source of build_workflow, the same way the yield_output checks below read
# their classes: the wiring is a claim about the REAL graph, so the test must look at the real
# add_edge calls rather than at a list this file constructed itself.
wiring = inspect.getsource(fd.build_workflow)
edge_positions = [wiring.find("add_edge(intake, investigate)"),
                  wiring.find("add_edge(investigate, challenge)"),
                  wiring.find("add_edge(challenge, refer)")]
check("the graph wires intake -> investigate -> challenge -> refer",
      -1 not in edge_positions and edge_positions == sorted(edge_positions),
      f"positions {edge_positions}")
check("the workflow builds", fd.build_workflow()[0] is not None)

# A checkpoint tags every value `module:QualName` and refuses anything not on the allowlist. If the
# types were defined in the entry-point script the tag would be `__main__:...`, and a checkpoint
# written by one command would be unreadable by the next - silently, as an empty list.
check("checkpoint allowlist is module-qualified, not __main__",
      all(x.startswith("m3_types:") for x in t.CHECKPOINT_TYPES))
every_type = {f"m3_types:{c.__name__}" for c in vars(t).values()
              if dataclasses.is_dataclass(c) and getattr(c, "__module__", "") == "m3_types"}
check("every message type is on the checkpoint allowlist",
      every_type and every_type <= set(t.CHECKPOINT_TYPES),
      f"missing {sorted(every_type - set(t.CHECKPOINT_TYPES))}")

print("\n=== nothing is referred without a person ===")

# The question an insurer would ask, answered from the structure of the code itself.
src = {c.__name__: inspect.getsource(c)
       for c in (fd.Intake, fd.Investigate, fd.Challenge, fd.Refer)}
yielders = [n for n, s in src.items() if "yield_output" in s]
check("only Refer can produce an outcome", yielders == ["Refer"], f"found {yielders}")
check("Refer only yields inside its response handler, i.e. after a human answered",
      "yield_output" in src["Refer"].split("@response_handler")[-1]
      and "yield_output" not in src["Refer"].split("@response_handler")[0])
check("the desk asks for a decision before it can finish", "request_info" in src["Refer"])

# The runner refuses to act without a name: a referral raised by "unnamed" would defeat the point
# of having a human gate at all. Run it for real, in a child process, rather than grepping the
# source for the error message - the guard exits before any sign-in or model call, so this is free.
guard = subprocess.run([sys.executable, str(Path(__file__).resolve().parent / "m3_run.py"),
                        "approve"], capture_output=True, text=True, timeout=120)
check("approve without --by is refused, in a real process",
      guard.returncode != 0 and "named human" in (guard.stderr + guard.stdout),
      f"rc={guard.returncode} out={((guard.stderr + guard.stdout)[:80])!r}")

print("\n=== the folder is self-contained ===")

# Nothing in this folder may import anything outside it, except installed packages. A cross-folder
# import works wherever that folder happens to exist and fails where it does not - and one inside a
# function fails only when that function first runs.
HERE = Path(__file__).resolve().parent
LOCAL = {p.stem for p in HERE.glob("m3_*.py")}
cross_module, unresolved, path_hacks = [], [], []
for p in sorted(HERE.glob("m3_*.py")):
    body = p.read_text(encoding="utf-8")
    for stmt in re.findall(r"^[ \t]*(?:from|import)[ \t]+(m\d_[A-Za-z0-9_]+)", body, re.M):
        if stmt.startswith("m3_"):
            if stmt not in LOCAL:
                unresolved.append(f"{p.name} -> {stmt}")
        else:
            cross_module.append(f"{p.name} -> {stmt}")
    for line in re.findall(r"^[ \t]*sys\.path\.insert.*$", body, re.M):
        if re.search(r"[\"']Module\d", line):
            path_hacks.append(f"{p.name}: {line.strip()[:60]}")

check("no file imports another module's Python", not cross_module, f"{cross_module}")
check("every local import resolves to a file in this folder", not unresolved, f"{unresolved}")
check("no sys.path hack reaches into another module's folder", not path_hacks, f"{path_hacks}")

print("\n=== the shape of what a model returns is ENFORCED, not requested ===")

# Requesting JSON in the prompt is not the same as enforcing a schema: a drifting model returns
# well-formed JSON carrying a different JUDGMENT, which is far harder to spot. Checked this way
# because a schema constant can be perfectly correct and never actually passed to a model call.
for node, schema in (("Investigate", "TRAVERSAL_SCHEMA"), ("Challenge", "JUDGEMENT_SCHEMA")):
    check(f"{node} enforces a response schema",
          "response_format" in src[node] and schema in src[node])
# The service keeps a Responses conversation for 30 days unless told not to, and these carry
# claimants' names and bank accounts.
check("model calls ask the service not to store the conversation",
      fd.AGENT_OPTIONS.get("store") is False
      and "AGENT_OPTIONS" in inspect.getsource(fd.investigator_agent)
      and "AGENT_OPTIONS" in inspect.getsource(fd.challenger_agent))

print("\n=== the money ===")
# Prove the arithmetic, not the source text: the pricing function is pure, so it can be run
# against fixture rows right here, with no Dataverse and no model.
fixture = [{"cp_bookclaimref": "BCL-TEST-1", "cp_amountclaimed": "3100"},
           {"cp_bookclaimref": "BCL-TEST-2", "cp_amountclaimed": 2500.5}]
refs, total = fd.price_confirmed(fixture)
check("exposure is summed in Python from claim rows, not returned by a model",
      refs == ["BCL-TEST-1", "BCL-TEST-2"] and total == 5600.5, f"got {refs}, {total}")
check("no schema lets a model return an amount",
      "amount" not in str(fd.JUDGEMENT_SCHEMA) and "exposure" not in str(fd.TRAVERSAL_SCHEMA))

print("\n=== the decision table ===")
cases = [
    (True, True, "referred"),      # a person agreed with the desk
    (True, False, "not_referred"),  # the desk cleared them and a person agreed
    (False, True, "dismissed"),    # a person overrode a recommendation to refer
    (False, False, "dismissed"),   # a person overrode a recommendation not to refer
]
for approved, recommended, want in cases:
    got = fd.decide_outcome(approved, recommended)
    check(f"decide_outcome(approved={approved}, recommended={recommended}) == {want}", got == want,
          f"got {got}")

print("\n=== what the models may name ===")
# The models write claimant refs into their replies, and the people under investigation wrote the
# data those models read. Only refs the records produced may go forward.
reached = {"CLT-9001", "CLT-9002", "CLT-9003"}
kept = fd.known_refs(["clt-9002", "CLT-9002", "CLT-0999", "CLT-9001"], reached)
check("a name no tool returned is not a candidate",
      kept == ["CLT-9002", "CLT-9001"], f"got {kept}")
confirmable = fd.known_refs(["CLT-9001", "CLT-9004", "clt-9001"], ["CLT-9001", "CLT-9002"])
check("the challenger can confirm only candidates, each once",
      confirmable == ["CLT-9001"], f"got {confirmable}")
check("a referral needs at least three confirmed claimants, decided in code",
      fd.should_refer(True, fd.MIN_TO_REFER) and not fd.should_refer(True, fd.MIN_TO_REFER - 1)
      and not fd.should_refer(False, 9))
listing = fd.challenge_listing([t.LinkedClaimant("CLT-9002", "A Person", "shares an account",
                                                 "bank_account", ["CLT-9001"])])
check("the challenger is shown who each candidate shares an identifier with",
      "shares bank_account with CLT-9001" in listing, f"got {listing!r}")
# A person the investigator labelled as a postcode match, whom the records also link by a bank
# account: the challenger must see the bank account, or it excludes a ring member.
listing = fd.challenge_listing([t.LinkedClaimant(
    "CLT-9006", "A Person", "same postcode", "postcode", ["CLT-9005"],
    evidence=["bank_account with CLT-9005", "postcode with CLT-9005"])])
check("the challenger sees every link the records hold, not only the investigator's label",
      "shares bank_account with CLT-9005; shares postcode with CLT-9005" in listing,
      f"got {listing!r}")

print("\n=== the guardrails ===")
for fn in fd.LINK_TOOLS:
    name = getattr(fn, "name", None) or getattr(fn, "__name__", str(fn))
    cap = getattr(fn, "max_invocations", None)
    check(f"tool {name} has a call ceiling", isinstance(cap, int) and cap > 0, f"got {cap}")

for fn in fd.LINK_TOOLS:
    fn.invocation_count = fn.max_invocations
fd.reset_tool_limits()
check("tool call limits start again for each investigation",
      all(fn.invocation_count == 0 for fn in fd.LINK_TOOLS))


async def run_guard(error):
    """Run the tool-failure middleware around a tool that raises `error`."""
    g = fd.ToolFailureGuard()
    ctx = SimpleNamespace(function=SimpleNamespace(name="expand"), result=None)

    async def call_next():
        raise error
    try:
        await g.process(ctx, call_next)
        stopped = False
    except MiddlewareTermination:
        stopped = True
    return g, ctx, stopped

g, ctx, stopped = asyncio.run(run_guard(RuntimeError("Dataverse read failed 403")))
check("a failing tool stops the investigation instead of shrinking it",
      stopped and len(g.failures) == 1 and "403" in g.failures[0], f"{stopped} {g.failures}")
g, ctx, stopped = asyncio.run(run_guard(ToolException("Function 'expand' has reached its maximum "
                                                      "invocation limit")))
check("a tool's call limit is told to the model, not treated as a failure",
      not stopped and not g.failures and "maximum" in str(ctx.result), f"{stopped} {g.failures}")
fd.TOOL_LOG.clear()

print("\n=== the data layer ===")
# The book contains a repairer called Eccles Panel & Paint. Unencoded, the & splits the query
# string and the filter fails as malformed - so the escape helper must survive both parsers,
# OData's and the URL's. This is the regression test for exactly that name.
encoded = dv.esc("Eccles Panel & Paint")
check("identifier values are URL-encoded before they reach Dataverse",
      "&" not in encoded and "%26" in encoded and " " not in encoded, f"got {encoded!r}")

print("\n=== the judgment the desk is about ===")
check("the challenger is told a postcode alone is not evidence",
      re.search(r"postcode", fd.CHALLENGER, re.I) is not None
      and "Exclude anyone whose ONLY link is a postcode" in fd.CHALLENGER)
check("the investigator is told a shared repairer is not a link",
      "not a link" in fd.INVESTIGATOR)
check("the investigator is bounded to a fixed number of hops",
      f"at most {fd.MAX_HOPS} hops" in fd.INVESTIGATOR)

print(f"\n{len(PASS)} passed, {len(FAIL)} failed")
sys.exit(1 if FAIL else 0)
