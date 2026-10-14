"""Configuration for the fraud desk, read from `lab/.env`. The Python twin of `Env.cs`.

DEMONSTRATION CODE - written to be read rather than deployed.

The endpoint, tenant and Dataverse org say *where* things live and grant nothing on their own,
but they are yours to supply: `lab/.env.sample` ships them as <placeholders>, and every reader
below treats an unreplaced placeholder as MISSING. That is deliberate. A default that silently
stood in for one of them would sign you in to a directory you are not a member of, and the only
symptom is an authentication error that names nothing.

Model calls are keyless by default, paid for by the same sign-in that reads the claims book.
`FOUNDRY_KEY` is an optional override; see `foundry()`.
"""
import os
from pathlib import Path

_cache: dict | None = None


def unquote(value: str) -> str:
    """The value without one matching pair of surrounding quotes, as many .env files write them.

    The same rule in all three readers of lab/.env (this file, lab/verify_env.py and the C# track's
    Env.cs), so a value that one of them accepts, the others read the same way.
    """
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1]
    return value


def lab_env() -> dict:
    """Parse `lab/.env`, searching upward from this file rather than the working directory.

    Returns an empty dict when there is no file; only `foundry()` treats that as fatal.
    """
    global _cache
    if _cache is not None:
        return _cache
    _cache = {}
    here = Path(__file__).resolve()
    for parent in [here.parent, *here.parents]:
        for candidate in (parent / "lab" / ".env", parent / ".env"):
            if candidate.is_file():
                for line in candidate.read_text(encoding="utf-8").splitlines():
                    line = line.strip()
                    if line and not line.startswith("#") and "=" in line:
                        k, v = line.split("=", 1)
                        _cache[k.strip()] = unquote(v)
                return _cache
    return _cache


def setting(name: str, default: str = "") -> str:
    """Process environment first, then `lab/.env`, then the default.

    A value still wrapped in <angle brackets> is the sample's placeholder and counts as UNSET,
    the same convention `foundry()` has always used for `FOUNDRY_KEY`. Half-editing the file is
    the common mistake, and this is what turns it into an error that names the line.
    """
    value = os.environ.get(name) or lab_env().get(name) or default
    return "" if "<" in value and ">" in value else value


def dataverse_target() -> tuple[str, str]:
    """`(tenant, org)`, or exit naming whichever is still a placeholder.

    RAISES on purpose. There is no sensible default for "which directory am I": guessing
    one signs you in to a tenant you are not a member of, and Entra's answer
    (AADSTS50020) names the tenant rather than the setting, so the mistake reads as a
    broken lab rather than an unedited config file.

    The trailing slash is stripped: a pasted URL often carries one, and
    `{org}//api/data/v9.2` 404s without mentioning the extra slash.
    """
    tenant, org = setting("DATAVERSE_TENANT"), setting("DATAVERSE_ORG")
    missing = [n for n, v in (("DATAVERSE_TENANT", tenant), ("DATAVERSE_ORG", org)) if not v]
    if missing:
        raise SystemExit(
            "lab/.env is missing, or still holds the sample's <placeholder> for "
            + ", ".join(missing) + ".\n"
            "  From the repository root:  cp lab/.env.sample lab/.env\n"
            "  Then put your own tenant id and Dataverse org URL there, angle brackets removed.\n"
            "  The tenant id is on the Entra ID overview page; the org URL is your\n"
            "  environment's, ending .crm<n>.dynamics.com.\n"
            "  Chapter 1 of the book says where both come from.")
    return tenant, org.rstrip("/")


REQUIRED_FOUNDRY = ("FOUNDRY_OPENAI_V1", "MODEL_DEPLOYMENT")


def foundry() -> dict:
    """The Foundry endpoint and deployment, plus `FOUNDRY_KEY` when a real one is set.

    `FOUNDRY_KEY` is optional and normally empty: with no key, the desk uses your sign-in for its
    model calls too (`m3_dataverse.foundry_token_provider`), so there is no key to hand out and
    nothing shared. A real value here - your own resource's key, or a token you minted
    yourself - overrides that.
    The sample's `<placeholder>` counts as unset, so an untouched lab/.env is keyless.
    """
    missing = [n for n in REQUIRED_FOUNDRY if not setting(n)]
    if missing:
        raise SystemExit(
            "lab/.env is missing, or still holds a <placeholder> for, " + ", ".join(missing) + ".\n"
            "  From the repository root:  cp lab/.env.sample lab/.env\n"
            "  Then replace the placeholders with your own Foundry account and deployment -\n"
            "  Chapter 1 of the book, Creating the Microsoft Foundry resources, says where they come from.")
    key = setting("FOUNDRY_KEY")
    if "<" in key:
        key = ""
    return {**{n: setting(n) for n in REQUIRED_FOUNDRY}, "FOUNDRY_KEY": key}
