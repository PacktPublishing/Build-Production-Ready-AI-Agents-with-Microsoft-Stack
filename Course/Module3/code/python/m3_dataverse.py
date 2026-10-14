"""Dataverse access for the fraud desk: sign in as yourself, read the book, write the referral.

DEMONSTRATION CODE - written to be read rather than deployed. See `m3_frauddesk.py` for what a
production system would add.

TWO THINGS HERE ARE THE POINT, NOT PLUMBING

1. You sign in as YOU. No shared secret, no service account, so the agent's tools run with your
   permissions. "What can your agent see?" answers itself: exactly what its caller can see.
2. The desk never changes the claims book. Everything it writes is a new row stamped with the
   investigator's user name, so two investigations never overwrite each other.

Sections, in the order the desk uses them: configuration, sign-in, http, the book, the referral.
"""
from __future__ import annotations

import asyncio
import json
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from pathlib import Path

from azure.core.exceptions import ClientAuthenticationError
from azure.identity import (
    AuthenticationRecord, AuthenticationRequiredError, DeviceCodeCredential,
    TokenCachePersistenceOptions, get_bearer_token_provider,
)

import m3_env


# --------------------------------------------------------------------------- configuration
# Tenant and org live in `lab/.env` (see `m3_env.py`) because they are environment-specific.
# CLIENT_ID does not: it is the Azure CLI's public client, the same in every tenant, which is why
# the sign-in page asks "Are you trying to sign in to Microsoft Azure CLI?".
CLIENT_ID = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"

# The model endpoint's resource. A constant like CLIENT_ID, not config: it names Azure's
# Cognitive Services API surface and is the same value in every tenant.
FOUNDRY_SCOPE = "https://cognitiveservices.azure.com/.default"

_target: tuple[str, str] | None = None


def target() -> tuple[str, str]:
    """`(tenant, org)`, resolved on FIRST USE rather than at import.

    Deliberately lazy. `m3_test.py` imports this module and never touches Dataverse - it is the
    offline suite to run before anything else - so an unedited `lab/.env` must not stop it. The
    error still arrives, loudly and by name, the moment something actually needs a directory.
    """
    global _target
    if _target is None:
        _target = m3_env.dataverse_target()
    return _target


def org_url() -> str:
    """The Dataverse environment's URL, as configured in `lab/.env`."""
    return target()[1]


def api() -> str:
    """The Web API base for the configured org."""
    return f"{org_url()}/api/data/v9.2"


# --------------------------------------------------------------------------- sign-in
# Microsoft's identity library (azure-identity, built on MSAL) does the OAuth work: the device-code
# flow, the token refresh and the token cache. Two files take part, and neither holds a secret
# you can read:
#
#   .m3_token.json   beside this file: WHO is signed in (tenant, account, user name). No token.
#                    Delete it to sign out, or to sign in as somebody else - the next command then
#                    asks you to run m3_login.py again.
#   the token cache  in your user profile, managed by the library. Encrypted by the operating
#                    system where it can be (Windows, macOS, a Linux desktop with a keyring); where
#                    it cannot, as in a Codespace, it falls back to a file only your account can
#                    read. It holds a refresh token for the Azure CLI's client, which can be
#                    exchanged for other Azure resources too, not only this claims book - so treat
#                    your machine's account as the thing protecting it.
SESSION_FILE = Path(__file__).resolve().parent / ".m3_token.json"
CACHE = TokenCachePersistenceOptions(name="claims365-fraud-desk", allow_unencrypted_storage=True)

_credential: DeviceCodeCredential | None = None
_user: str = ""


def _show_code(verification_uri: str, user_code: str, expires_on) -> None:
    """What you see while signing in. The address and the code come from Microsoft Entra."""
    print("=" * 72)
    print("  SIGN IN AS YOURSELF")
    print("=" * 72)
    print(f"  1. open {verification_uri}")
    print(f"  2. enter code  {user_code}")
    print("  3. sign in with your own account, the one holding the Claims 365 Attendee role")
    print("=" * 72)


def device_login() -> None:
    """Interactive, and needed once: until the cached session expires or you delete it.

    Device code is the flow that works everywhere this code runs: a Codespace has no browser to
    redirect to and cannot listen on localhost. `authenticate` signs in and returns a record of WHO
    signed in; the tokens themselves go to the library's cache, never to this folder.
    """
    tenant, org = target()
    cred = DeviceCodeCredential(client_id=CLIENT_ID, tenant_id=tenant, prompt_callback=_show_code,
                                cache_persistence_options=CACHE)
    try:
        record = cred.authenticate(scopes=[f"{org}/.default"])
    except ClientAuthenticationError as exc:
        raise SystemExit("sign-in failed: " + _first_line(exc)) from None
    SESSION_FILE.write_text(record.serialize(), encoding="utf-8")
    global _credential, _user
    _credential, _user = None, ""          # the next call builds the silent credential from the file
    print("signed in. session saved next to the code (gitignored); "
          "tokens kept in your user profile's token cache.")


def signed_in() -> bool:
    """Whether a session file exists. Cheap: it reads nothing and calls nobody."""
    return SESSION_FILE.exists()


def credential() -> DeviceCodeCredential:
    """The signed-in credential for THIS process, built silently from the session file.

    `disable_automatic_authentication` is the important flag: a command that needs a token and
    cannot get one silently stops with a sentence, rather than starting a sign-in in the middle of
    an investigation.
    """
    global _credential, _user
    if _credential is not None:
        return _credential
    if not SESSION_FILE.exists():
        raise SystemExit("not signed in. run:  python m3_login.py")
    try:
        record = AuthenticationRecord.deserialize(SESSION_FILE.read_text(encoding="utf-8"))
    except (ValueError, KeyError, TypeError):
        raise SystemExit("the session file .m3_token.json is unreadable. Delete it and run:  "
                         "python m3_login.py") from None
    if record.tenant_id.lower() != target()[0].lower():
        raise SystemExit("you are signed in to a different tenant from the one in lab/.env. "
                         "Delete .m3_token.json and run:  python m3_login.py")
    _credential = DeviceCodeCredential(client_id=CLIENT_ID, tenant_id=record.tenant_id,
                                       authentication_record=record,
                                       cache_persistence_options=CACHE,
                                       disable_automatic_authentication=True)
    _user = record.username
    return _credential


def _first_line(exc: Exception) -> str:
    """The first line of an identity error, which carries the AADSTS code and its meaning."""
    return (str(exc).strip().splitlines() or ["unknown error"])[0][:300]


def _token(scope: str) -> str:
    """An access token for `scope` from the cached sign-in, refreshed by the library as needed."""
    try:
        return credential().get_token(scope).token
    except AuthenticationRequiredError:
        raise SystemExit("session expired. run:  python m3_login.py") from None
    except ClientAuthenticationError as exc:
        # Kept, not deleted: a Conditional Access block or a missing consent is not fixed by signing
        # in again, and the AADSTS code in this line is what says which it is.
        raise SystemExit("token request refused: " + _first_line(exc)) from None


def token() -> str:
    """A Dataverse token for the configured environment."""
    return _token(f"{org_url()}/.default")


def foundry_token_provider():
    """An async callable that returns a model-call token from the SAME sign-in, so nobody hands you
    a key.

    Passed to the chat client as its `api_key`: the client awaits it whenever it needs a token, so a
    long run never holds an expired one. The library's provider is synchronous, so it runs in a
    worker thread rather than blocking the workflow while a token is refreshed. Your model calls are
    authorized - and attributable - as you, exactly like your data reads. A real FOUNDRY_KEY in
    lab/.env overrides this.
    """
    get = get_bearer_token_provider(credential(), FOUNDRY_SCOPE)

    async def provider() -> str:
        return await asyncio.to_thread(get)
    return provider


def whoami() -> str:
    """Your user name, from the session record. It stamps every referral row."""
    credential()
    return _user or "unknown"


# --------------------------------------------------------------------------- http
def _retry_after(headers, attempt: int) -> float:
    """How long to wait before retrying: the server's Retry-After when it gives one, else a backoff.

    Dataverse's service-protection limits answer 429 with a Retry-After header, and retrying sooner
    only extends the block.
    """
    value = headers.get("Retry-After") if headers else None
    if value:
        try:
            return min(float(value), 60.0)
        except ValueError:
            try:
                return max(0.0, min((parsedate_to_datetime(value) - datetime.now(timezone.utc))
                                    .total_seconds(), 60.0))
            except (TypeError, ValueError):
                pass
    return 2.0 * attempt


def _req(method: str, path: str, body=None):
    """One Dataverse Web API call, with the OData headers and the retry rule. `(status, json)`."""
    if "?" in path:
        # Encode what remains of the OData syntax - the spaces around `eq` and `or` - while
        # leaving the query structure ($, &, =) and anything `esc` already encoded (%) alone.
        # The space must NOT be in `safe`: an unencoded space reaches http.client as a control
        # character, and the exception names the URL but not the cause. String values were
        # percent-encoded by `esc` when the filter was built, so a raw & here is always a
        # parameter separator, never data.
        base, q = path.split("?", 1)
        path = base + "?" + urllib.parse.quote(q, safe="$&=(),'/*%")
    req = urllib.request.Request(f"{api()}/{path}",
                                 data=json.dumps(body).encode() if body is not None else None,
                                 method=method)
    req.add_header("Authorization", f"Bearer {token()}")
    req.add_header("Accept", "application/json")
    req.add_header("OData-MaxVersion", "4.0")
    req.add_header("OData-Version", "4.0")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    # The retry rule. A throttled or briefly unavailable service (429, 502, 503, 504) is retried for
    # every method, because the server says it did not do the work. A connection that failed or
    # timed out is retried for reads only: a POST may have landed before the connection dropped,
    # and repeating it would write a second row. A 400 or 403 comes straight back, since repeating
    # it only delays the explanation.
    last = None
    for attempt in range(1, 5):
        try:
            with urllib.request.urlopen(req, timeout=90) as r:
                raw = r.read().decode()
                return r.status, (json.loads(raw) if raw else {})
        except urllib.error.HTTPError as e:
            raw = e.read().decode()
            if e.code in (429, 502, 503, 504) and attempt < 4:
                time.sleep(_retry_after(e.headers, attempt))
                continue
            return e.code, (json.loads(raw) if raw else {})
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            last = e
            if method == "GET" and attempt < 4:
                time.sleep(2 * attempt)
                continue
            break
    raise RuntimeError(
        f"Dataverse unreachable: {last}. Check your connection. A paused investigation is safe on "
        f"disk; if this happened while writing a referral, look in Dataverse before writing it again.")


def query(path: str) -> list[dict]:
    """GET an OData path and return the rows, raising on anything that is not a 200.

    Raising matters: a tool that swallowed a 403 would report "nobody is connected to this
    claimant", and a wrong answer that looks right is the worst outcome here. The desk's tool
    middleware turns that exception into a failed investigation rather than a smaller one.

    One page only. Dataverse returns up to 5,000 rows a page and the claims book holds 234, so the
    desk never needs `@odata.nextLink`; a larger book would.
    """
    s, r = _req("GET", path)
    if s != 200:
        raise RuntimeError(f"Dataverse read failed {s}: {str(r)[:300]}")
    return r.get("value", [])


def esc(v: str) -> str:
    """A string value made safe for an OData filter: quotes doubled, then percent-encoded.

    Two parsers see this value, and each has a character that breaks it. OData escapes a single
    quote by doubling it - a claimant called O'Neill would otherwise produce a syntax error that
    reads like a permissions problem. The URL parser splits on & - a repairer called
    Eccles Panel & Paint would otherwise truncate the query at the ampersand and fail as a
    malformed filter. Encoding here, once, covers both.
    """
    return urllib.parse.quote(str(v).replace("'", "''"), safe="")


# --------------------------------------------------------------------------- the book, read-only
# Named once so every query returns the same shape and adding a column is one edit.
CLAIM_COLS = ("cp_bookclaimref,cp_claimantref,cp_repairername,cp_claimtype,"
              "cp_amountclaimed,cp_claimstatus,cp_incidentdate,cp_incidentpostcode")
CLAIMANT_COLS = "cp_claimantref,cp_fullname,cp_email,cp_phone,cp_bankaccount,cp_postcode"


def get_claim(claim_ref: str) -> dict | None:
    """One claim by reference, or None. Intake uses it to refuse a claim that does not exist."""
    rows = query(f"cp_bookclaims?$select={CLAIM_COLS}"
                 f"&$filter=cp_bookclaimref eq '{esc(claim_ref)}'")
    return rows[0] if rows else None


def get_claimant(claimant_ref: str) -> dict | None:
    """One claimant by reference, or None. This is where the identifiers a ring shares live."""
    rows = query(f"cp_claimants?$select={CLAIMANT_COLS}"
                 f"&$filter=cp_claimantref eq '{esc(claimant_ref)}'")
    return rows[0] if rows else None


def claimants_sharing(attribute: str, value: str) -> list[dict]:
    """Everyone holding one exact identifier value. The single-value form of a hop.

    The attribute names are the agent-facing vocabulary, so a tool description never has to teach
    the model a Dataverse schema.
    """
    col = {"bank_account": "cp_bankaccount", "phone": "cp_phone",
           "postcode": "cp_postcode", "email": "cp_email"}[attribute]
    return query(f"cp_claimants?$select={CLAIMANT_COLS}&$filter={col} eq '{esc(value)}'")


def claims_of(claimant_ref: str) -> list[dict]:
    """Every claim belonging to one claimant."""
    return query(f"cp_bookclaims?$select={CLAIM_COLS}"
                 f"&$filter=cp_claimantref eq '{esc(claimant_ref)}'")


def claims_by_repairer(repairer: str) -> list[dict]:
    """Every claim at one repairer. Joined by NAME, so the book needs no relationship metadata."""
    return query(f"cp_bookclaims?$select={CLAIM_COLS}"
                 f"&$filter=cp_repairername eq '{esc(repairer)}'")


# --------------------------------------------------------------------------- batched reads
# A hop is a set operation: everyone who shares an identifier with anyone found so far. Doing it one
# person at a time costs a model round trip each, and round trips are what an agent's bill is made
# of. These do a whole hop in one query per identifier type, however many people are in it.
# Far above anything this book can produce. It exists because these values are chosen by a MODEL,
# and asking about hundreds at once builds a URL past what Dataverse accepts, failing with an error
# that never mentions length.
MAX_FILTER_TERMS = 150


def _or_filter(column: str, values) -> str:
    """`col eq 'a' or col eq 'b' or ...`. Empty string when there is nothing to ask.

    Sorted and de-duplicated so the same hop always produces the same URL. Callers must treat "" as
    "ask nothing": a bare `$filter=` is a 400.
    """
    uniq = sorted({v for v in values if v})
    if len(uniq) > MAX_FILTER_TERMS:
        raise ValueError(
            f"asked about {len(uniq)} values in one query, more than the limit of "
            f"{MAX_FILTER_TERMS}. A filter that long exceeds the URL length Dataverse accepts, "
            f"and fails with an error that does not mention length.")
    return " or ".join(f"{column} eq '{esc(v)}'" for v in uniq)


def claimants_by_refs(refs) -> list[dict]:
    """Many claimants in one query. The first half of a hop: who are the people we already have?"""
    f = _or_filter("cp_claimantref", refs)
    if not f:
        return []
    return query(f"cp_claimants?$select={CLAIMANT_COLS}&$filter={f}")


def claimants_sharing_any(attribute: str, values) -> list[dict]:
    """Everyone holding ANY of these values. The second half of a hop.

    Bank account, phone and postcode only - the three the challenge step knows how to weigh.
    """
    col = {"bank_account": "cp_bankaccount", "phone": "cp_phone",
           "postcode": "cp_postcode"}[attribute]
    f = _or_filter(col, values)
    if not f:
        return []
    return query(f"cp_claimants?$select={CLAIMANT_COLS}&$filter={f}")


def claims_of_many(refs) -> list[dict]:
    """Every claim belonging to any of these claimants, in one query."""
    f = _or_filter("cp_claimantref", refs)
    if not f:
        return []
    return query(f"cp_bookclaims?$select={CLAIM_COLS}&$filter={f}")


# --------------------------------------------------------------------------- the referral, write
def new_run_id() -> str:
    """A UTC timestamp identifying one investigation. Scoped to one person by the referral ref."""
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")


def write_referral(*, referral_ref, seed_claim, decision, ring_size, claim_count, exposure,
                   hops, investigator, approved_by, evidence, language, run_id) -> bool:
    """Write the referral header: one row per investigation.

    Keyword-only on purpose: a dozen positional arguments, several of them integers, is the shape
    where a transposition silently writes `ring_size` into `claim_count`. `investigator` is the
    account that STARTED the investigation, carried through the checkpoint, which may differ from
    the account that approved it. `approved_by` is the name typed with --by.
    """
    s, r = _req("POST", "cp_fraudreferrals", {
        "cp_referralref": referral_ref,
        "cp_seedclaimref": seed_claim,
        "cp_decision": decision,
        "cp_ringsize": int(ring_size),
        "cp_claimcount": int(claim_count),
        "cp_exposure": float(exposure),
        "cp_hops": int(hops),
        "cp_investigator": investigator,
        "cp_approvedby": approved_by,
        "cp_language": language,
        "cp_runid": run_id,
        "cp_evidence": evidence[:3999],
    })
    if not (200 <= s < 300):
        raise RuntimeError(f"referral write failed {s}: {str(r)[:300]}")
    return True


def write_referral_claim(*, referral_ref, claim_ref, claimant_ref, reason, strength) -> None:
    """One evidence line: this claim is in this referral, and why.

    These make the referral auditable rather than a summary row with a number on it.
    """
    s, r = _req("POST", "cp_referralclaims", {
        "cp_referralclaimref": f"{referral_ref}|{claim_ref}"[:139],
        "cp_referralref": referral_ref,
        "cp_bookclaimref": claim_ref,
        "cp_claimantref": claimant_ref,
        "cp_linkreason": reason[:199],
        "cp_linkstrength": strength,
    })
    if not (200 <= s < 300):
        raise RuntimeError(f"referral line write failed {s}: {str(r)[:300]}")


def my_referrals() -> list[dict]:
    """Every referral YOU investigated, newest first.

    The payoff of stamping the user name: "mine" is a filter rather than a search.
    """
    me = esc(whoami())
    return query("cp_fraudreferrals?$select=cp_referralref,cp_decision,cp_ringsize,"
                 "cp_claimcount,cp_exposure,cp_hops,cp_approvedby,cp_language,cp_runid"
                 f"&$filter=cp_investigator eq '{me}'&$orderby=createdon desc")
