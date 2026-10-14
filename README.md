# Build-Production-Ready-AI-Agents-with-Microsoft-Stack

The code and setup files used by the book's chapters. Everything here runs in **your own** tenant:
your Power Platform environment, your Copilot Studio agent, your Microsoft Foundry resource and
your Azure subscription. Start with Chapter 1. It sets the tenant up and tells you which of these
folders each chapter needs, and in what order.

## What is here

| Folder | Chapters | What it holds |
|---|---|---|
| `Tenant-Setup/` | 1, then 2 to 5 | The setup pack: a Dataverse solution (10 tables, 6 flows and the Claims 365 Attendee security role), 432 rows of synthetic seed data as CSV files, the evaluation test set, and the instructions and response schema you paste into the agents. Start with `Tenant-Setup/Setup-Pack-Guide.html` |
| `Website/` | 3 and 4, optional | A WordPress plugin that adds the Copilot Studio chat widget and the Foundry claim form as two shortcodes, and the widget as one standalone page. Read `Website/README.md` first |
| `Course/Module2/evidence/` | 4 | The damage photographs and repair quotes for Case B and Case C. The photographs are openly licensed; their attribution is in `CREDITS.md`, which must stay with them |
| `lab/` | 1 and 5 | The pinned Python requirements, `.env.sample` and `verify_env.py` |
| `Course/Module3/code/python/` and `Course/Module3/code/dotnet/FraudDesk/` | 5 | The Fraud Desk, built with Microsoft Agent Framework: the workflow, its command line and its offline tests, in Python and in C# |
| `.devcontainer/` | 5 | The GitHub Codespace definition |

The `Course/Module2` and `Course/Module3` folder names are part of the paths the chapters print.
Chapters 2 to 4 are built in the browser; only Chapter 5 asks you to run code.

## Setting up the Fraud Desk for Chapter 5

Use the Codespace (**Code**, then **Codespaces**, then **Create codespace on main**), or your own
computer with Python 3.12 or 3.13 on Windows x64 or Linux x86-64 and, for the C# track, the .NET 8
SDK or later. On a Mac or an ARM machine, use the Codespace. Chapter 1's *Preparing the local Fraud
Desk runtime* section has the full steps, including the virtual environment and the Windows notes.

1. `cp lab/.env.sample lab/.env` (in Windows PowerShell, `Copy-Item lab/.env.sample lab/.env`). In
   a Codespace the copy and the dependency install have already been done for you. You still have
   to do step 2.
2. **Edit `lab/.env` and replace its four `<placeholders>`.** Three of them the lab reads, and it
   will not start until they are your own values: `FOUNDRY_OPENAI_V1`, `DATAVERSE_TENANT` and
   `DATAVERSE_ORG`. The fourth, `FOUNDRY_ENDPOINT`, is read by no code; it records where your
   deployment lives. The file says where each comes from, and so does Chapter 1. Nothing guesses on
   your behalf: a half-edited file stops with the setting named, rather than signing you in to a
   directory you are not a member of.

   **There is no key to paste.** Model calls are keyless: they reuse your sign-in
   (`python m3_login.py`, or `dotnet run -- login` for C#), the same session that reads the claims
   book. Leave `FOUNDRY_KEY` exactly as it is unless you want to override that.
3. `python lab/verify_env.py` should end `ENVIRONMENT READY`.

**Signing in uses device code.** A Conditional Access policy that blocks device code flow (Microsoft
ships a managed one) stops it. Chapter 1's runtime step tells you what to check before you sign in.

**Where the session lives.** Microsoft's identity library does the sign-in and keeps the tokens in
your user profile's token cache, encrypted by the operating system where it can be (in a Codespace,
a file only your account can read). The `.m3_token.json` beside the code (for C#, in
`bin/Debug/net8.0`) records only who signed in, and holds no token. Delete it to sign out or to
sign in as someone else. The cached refresh token belongs to the Azure CLI's public client and can
be exchanged for other Azure resources, so treat your machine account as what protects it.

## Check it works, offline and free

```bash
python lab/verify_env.py                                     # no credentials needed
cd Course/Module3/code/python && python m3_test.py           # offline, no model calls
```

The Python tests end `37 passed, 0 failed`. For C#, `dotnet run -- test` from
`Course/Module3/code/dotnet/FraudDesk` ends `33 passed, 0 failed`.

## Two options worth knowing

- `--note "..."` with `approve` or `decline` records a note in the audit trail and in the referral's
  evidence in Dataverse.
- `--telemetry` with `start`, `approve` or `decline` prints the framework's OpenTelemetry spans (each
  workflow step, agent run, model call and tool call, with timings and token counts) and, when
  `APPLICATIONINSIGHTS_CONNECTION_STRING` is set in the environment, also hands them to the Azure
  Monitor exporter for Application Insights (that export was not run for the book). Prompts and
  replies are not recorded.

---

Claims 365 and Contoso Insurance are training fiction. Every policy, claim, supplier and figure is
invented. The damage photographs are real and openly licensed; see
`Course/Module2/evidence/CREDITS.md`. The WordPress plugin in `Website/` declares GPL-2.0-or-later,
as WordPress plugins do; see `Website/README.md`.
