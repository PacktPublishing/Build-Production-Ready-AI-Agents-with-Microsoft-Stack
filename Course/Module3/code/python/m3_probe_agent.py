"""Does the framework's Agent work against this deployment, with tools and a strict schema?

DEMONSTRATION CODE - written to be read rather than deployed.

Three things about this model and this client are worth confirming before you trust a longer run,
because each of them fails quietly rather than loudly:

  1. gpt-5-mini rejects the older `max_tokens` parameter. The framework's Responses client turns the
     agent's `max_tokens` option into the Responses API's `max_output_tokens`. Does that work here?
  2. gpt-5-mini rejects a custom `temperature` even at 0.0. Does the client send one by default?
  3. Does `response_format` on an agent actually come back as parseable JSON?

Run it if the desk misbehaves and you want to know whether the cause is the model, the deployment
or the workflow. It uses the same client the desk uses. With neither a sign-in nor a real
FOUNDRY_KEY it SKIPS (exit 0) rather than failing: every check here is a live model call, so there
is nothing it can prove offline.
"""
import asyncio
import json

from agent_framework import tool

import m3_dataverse as dv
import m3_env
import m3_frauddesk as fd

CALLS = []


@tool(description="Return the policy excess in GBP for a Contoso policy number.")
def get_excess(policy_number: str) -> dict:
    CALLS.append(policy_number)
    return {"policy_number": policy_number, "excess_gbp": 500}


SCHEMA = {
    "type": "object", "additionalProperties": False,
    "required": ["excess_gbp", "source"],
    "properties": {"excess_gbp": {"type": "number"},
                   "source": {"type": "string"}},
}


async def main():
    results = {}
    options = fd.AGENT_OPTIONS

    # 1. plain agent, no options at all - does the client send a temperature we cannot use?
    a = fd._client().as_agent(name="probe", instructions="Answer in one short sentence.",
                              default_options=options)
    try:
        r = await a.run("Say OK and nothing else.")
        results["plain agent"] = f"OK -> {str(r)[:60]!r}"
    except Exception as e:
        results["plain agent"] = f"FAILED {type(e).__name__}: {str(e)[:160]}"

    # 2. with a token budget expressed as the agent's max_tokens option
    try:
        r = await a.run("Say OK and nothing else.", options={"max_tokens": 2000})
        results["max_tokens option"] = f"OK -> {str(r)[:60]!r}"
    except Exception as e:
        results["max_tokens option"] = f"FAILED {type(e).__name__}: {str(e)[:160]}"

    # 3. tools - does the model call them, and do we see it?
    b = fd._client().as_agent(name="probe-tools",
                              instructions="Look the excess up. Never guess it.",
                              tools=[get_excess], default_options=options)
    CALLS.clear()
    try:
        r = await b.run("What is the excess on MM-MOTOR-2291?")
        results["tool calling"] = f"OK -> {len(CALLS)} call(s) {CALLS} -> {str(r)[:80]!r}"
    except Exception as e:
        results["tool calling"] = f"FAILED {type(e).__name__}: {str(e)[:160]}"

    # 4. strict structured output
    c = fd._client().as_agent(name="probe-schema",
                              instructions="Reply with the requested JSON only.",
                              default_options=options)
    try:
        r = await c.run("The excess is 500 and it came from the policy record.",
                        options={"response_format": SCHEMA})
        parsed = json.loads(str(r))
        results["response_format"] = f"OK -> parsed {parsed}"
    except Exception as e:
        results["response_format"] = f"FAILED {type(e).__name__}: {str(e)[:160]}"

    # 5. both at once, which is what the desk actually needs
    try:
        r = await c.run("The excess is 500 and it came from the policy record.",
                        options={"response_format": SCHEMA, "max_tokens": 3000})
        results["schema + budget"] = f"OK -> {json.loads(str(r))}"
    except Exception as e:
        results["schema + budget"] = f"FAILED {type(e).__name__}: {str(e)[:160]}"

    print("=" * 78)
    for k, v in results.items():
        print(f"  {k:<20} {v}")
    print("=" * 78)
    failed = [k for k, v in results.items() if v.startswith("FAILED")]
    if failed:
        print(f"  PROBE FAILED - {len(failed)} of {len(results)} check(s) failed")
        return 1
    print("  PROBE OK - the deployment behaves as the desk expects")
    return 0


if __name__ == "__main__":
    # Skip, not fail, when there is no credential at all: no real FOUNDRY_KEY in lab/.env and no
    # sign-in. Before you sign in that is the normal state, and a red result here would say
    # "broken" about a machine that is fine. The offline check for this folder is m3_test.py.
    have_settings = all(m3_env.setting(n) for n in m3_env.REQUIRED_FOUNDRY)
    have_key = have_settings and bool(m3_env.foundry()["FOUNDRY_KEY"])
    if not have_settings or (not have_key and not dv.signed_in()):
        print("PROBE SKIPPED - no credential yet, and every check here is a live model call.")
        print("Sign in first (python m3_login.py) and the probe uses that session. A real")
        print("FOUNDRY_KEY pasted into lab/.env works too, and overrides the sign-in.")
        raise SystemExit(0)
    raise SystemExit(asyncio.run(main()))
