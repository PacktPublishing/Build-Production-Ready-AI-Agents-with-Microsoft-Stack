# The Contoso Insurance website, as something you can run

The two website integrations from **Build Production-Ready AI Agents with the Microsoft Stack**,
packaged so they run on your own WordPress site.

- **A chat widget** in the corner of a page, talking to your **Copilot Studio** agent over
  Direct Line, so it answers from the grounding *you* built (Chapter 3 of the book).
- **A claim form** that takes a claim and two images, runs them past your **Microsoft Foundry**
  agent, shows the typed verdict a few seconds later and writes it to Dataverse (Chapter 4).

Both are optional: the book's exercises can be completed without a website.

> **Contoso Insurance is training fiction.** It is not a company, and no real claim, policy,
> policyholder or repairer appears anywhere in this package. The insurance framing is a worked
> example. The integration underneath it is real and is the part worth keeping.

---

## What is in the box

```
Website/
  README.md                          this file
  claims365-assistant.zip            ready to upload. Built from the folder below
  claims365-assistant/               the same plugin, unpacked, if you want to read it
    claims365-assistant.php          settings access, cache headers, the two includes
    uninstall.php                    deleting the plugin removes its options AND the secret
    includes/
      settings.php                   the admin page. Everything configurable lives here
      assistant.php                  [claims365_assistant]   the chat widget
      claim-form.php                 [claims365_claim_form]  the form, Foundry, Dataverse
    assets/
      assistant.css   assistant.js   the widget: Direct Line, markdown, sign-in, the face
      claim-form.css  claim-form.js  the form: previews, submit, poll, verdict card
  standalone/
    chat-widget.html                 the same widget with no WordPress at all. Paste and go
    build_standalone.py              regenerates it from the plugin's assets
  reference/                         Part 2 only. The chat widget needs none of this
    triage-agent-schema.json         the strict JSON schema. This is the contract
    dataverse-table.md               the optional table: columns, permissions, renaming
```

**There is no secret anywhere in this package.** Every id, hostname and key is a setting with an
empty default, so a fresh install reaches nothing at all until you fill the settings page in. There
is a section on [what is secret and what only looks it](#secrets-and-what-only-looks-like-one)
below, because getting that distinction wrong in either direction causes real problems.

---

## What you need before you start

You can do the two halves independently. Neither needs the other.

| For the chat widget | For the claim form |
|---|---|
| A published Copilot Studio agent | A Microsoft Foundry project with an agent in it |
| Its schema name and environment host | An Entra app registration, with a secret |
| Permission to make it anonymous | A role assignment on the Foundry resource |
| | Optionally, a Dataverse table |

And WordPress 6.x with PHP 8.0 or later. No Composer, no npm, no build step, no external
libraries: the whole plugin is five PHP files, two stylesheets and two scripts.

### What this costs, and what you have to be allowed to do

Check this first: two of them can stop you before you write a line of configuration.

**Everything below happens in *your* tenant and *your* subscription.**

**A Copilot Studio trial cannot publish an agent.** You can build one and use the test panel, and
then the Publish button refuses. This is stated plainly by Microsoft and it is the wall most
people hit. Publishing needs one of: a Microsoft 365 Copilot licence; a Copilot Studio User
Licence with credits allocated to the environment; a Copilot Studio User Licence with *Draw from
tenant pool* enabled; or an Office licence where the agent uses **no** generative AI features,
which this one does.
([Learn: billing and licensing FAQ](https://learn.microsoft.com/microsoft-copilot-studio/faq-billing-licensing))

**Pay-as-you-go alone is not enough either.** With PAYG enabled, a user still has to be in the
security group named in the tenant's **Copilot Studio authors** setting, or publishing fails with
a licence error that says nothing about security groups.
([Learn: licence errors when publishing](https://learn.microsoft.com/troubleshoot/power-platform/copilot-studio/licensing/publish-license-error))

**Then it meters.** Copilot Studio bills in **Copilot Credits** (renamed from *messages* on
1 September 2025, so older articles say "messages" for the same thing).
Every conversation costs, including a proactive greeting nobody replies to. Exceeding purchased
capacity can result in service denial rather than a bill. Microsoft publishes an
[agent usage estimator](https://microsoft.github.io/copilot-studio-estimator/); run your expected
traffic through it before you make an agent public.

**The claim form is separately metered**, per token, on an Azure subscription with a Microsoft
Foundry resource. Dataverse is optional and needs a Power Platform environment with a database.

**Rights you need, and may not have.** Creating an Entra app registration, assigning an Azure RBAC
role on the Foundry resource, and possibly changing a tenant-level Copilot Studio setting. If you
are a consultant working in a client's tenant, sort these out before you promise a date. None of
them are things you can grant yourself.

---

**Install it now, before configuring anything.** Upload **`claims365-assistant.zip`** (it sits
beside this README, ready to go) under Plugins, Add New, Upload Plugin, and activate it. If you
would rather build it yourself, zip the `claims365-assistant` **folder** so that folder is the
single entry at the top of the archive; zipping the folder that contains it will not install.
Then open **Settings, Claims 365 Assistant**. That page has a **Status** block at the foot which
tests each half live and tells you what is wrong in the words you need, so it is worth having open
while you work through the rest of this document.

---

## Part 1: the chat widget

### 1. Make the agent answer anonymously

This is the step that goes wrong, and it goes wrong in three different places.

**In the agent.** Settings, then **Security**, then **Authentication**, then **No authentication**,
then **Save**. Confirm the second dialog if you get one. Then **Publish**. The setting does not
take effect on the published agent until you publish again, which is why an agent can look
correctly configured and still refuse anonymous callers.

**In the tenant.** An admin control can block that choice for everybody. In the Power Platform
admin centre, under Security, Identity and access, sits **Authentication for agents (preview)**,
and only its **No authentication** option permits anonymous access at all. The other three
settings, *Require Microsoft authentication*, *Require Entra authentication* and *Allow all
supported authentication methods*, each block anonymous access while every other check you can run
still passes. A data policy can remove the option too.
([Learn: how admin settings map to user options](https://learn.microsoft.com/power-platform/admin/security/configure-authentication-controls-for-agents#how-admin-settings-map-to-user-options-in-copilot-studio))

**In what the agent is grounded on.** This one is easy to miss, and it forces an architecture
change:

> 🚨 **Dataverse knowledge and anonymous access are mutually exclusive.** An agent that has
> Dataverse tables attached as knowledge answers an anonymous caller with
> `AuthenticationNotConfigured`, whatever its authentication setting says. There is no warning and
> no setting that reconciles them.
>
> A public agent therefore has to be **tool-only**: instead of attaching the table, put the lookup
> behind a flow or a connector and attach that. It is more work and it is also better, because a
> typed tool returns the field you asked for rather than whatever a retrieval query happened to
> match. That was Chapter 2's central lesson and it started life as this error message.

Related, and from Microsoft's own documentation: if your agent has tools configured to require
user credentials, do not turn authentication off at the agent level, because those tools stop
working. ([Learn: turn off authentication](https://learn.microsoft.com/microsoft-copilot-studio/configuration-end-user-authentication#turn-off-authentication))

### 2. Find the two values

**The schema name.** In Copilot Studio: **Settings, Advanced, Metadata**. It looks like
`cr1a2_myAgentName`. It is not the display name, and it is unique only *within one environment*.

**The environment host.** Take your Power Platform environment's GUID, strip the hyphens, put a dot
before the last two characters, and append `.environment.api.powerplatform.com`:

```
  environment GUID   00001111-aaaa-2222-bbbb-3333cccc4444
  hyphens stripped   00001111aaaa2222bbbb3333cccc4444
  dot before last 2  00001111aaaa2222bbbb3333cccc44.44
  host               00001111aaaa2222bbbb3333cccc44.44.environment.api.powerplatform.com
```

The GUID above is an illustrative one, not a real environment. Use your own: Power Platform admin
centre, pick the environment, and it is in the URL and on the environment's detail page.

Because a schema name is unique only within an environment, the commonest failure is a perfectly
good agent name pointed at the wrong host, which answers **404** and reads exactly like a typo.
The Status block distinguishes the two: 404 means wrong environment or wrong name, and 401 or 403
means the agent is right there but is not published for anonymous access.

### 3. Put it on a page

Fill both values in on the settings page, set the title, greeting and up to four suggested
questions, and put `[claims365_assistant]` on any page. The widget floats bottom right, so the page
you choose barely matters. Anything else on the site can open it: a link to `#claims-assistant`, or
any element with `class="ci-open"`.

Then ask it something. If the Status block is green and the panel still says it cannot reach the
assistant, open the browser console: a refused token post or a shape-check failure prints there.

---

## Part 2: the claim form

### 1. Build the assessing agent

In the Foundry portal, create an agent, and paste in the **Triage instructions** from
`Tenant-Setup/module2/triage-agent-instructions.txt` in the tenant setup pack. Chapter 4 of the
book walks the same step.

Then set **Text format** to **JSON Schema** with the contents of
[`reference/triage-agent-schema.json`](reference/triage-agent-schema.json). *That* one is a
separate file for a practical reason rather than a tidy one: the schema runs to about 150 lines and
spans three pages of the PDF, so copying it out of the guide drags page headers and footers into
your JSON and the paste fails. Use the file.

**The schema is the contract.** The plugin reads `routing`, `consistency.verdict`,
`estimated_cost_band`, `damage.severity`, `document.total_claimed_gbp`, `fraud_suspected`,
`confidence` and `rationale` out of the returned JSON, and refuses the assessment outright if
`routing` is missing. All of it goes to Dataverse; only what the verdict card shows goes back to
the claimant, so the fraud flag and the rationale stay with your staff. Change the enums and you
must change `BAND` in `assets/claim-form.js` and the three routing classes in
`assets/claim-form.css` to match.

> ⚠️ **The Parameters panel is a draft surface.** A schema paste that silently failed leaves the
> placeholder saved, and the agent then correctly answers `{}` against it. Check the YAML tab, or
> the API, rather than the panel. It is the same trap as a form that looks filled in.

### 2. Create the identity the site authenticates as

A **confidential** app registration in Entra, with a client secret. This is a different
registration from the public one used for customer sign-in, and it should stay different: one
holds a secret and runs on your server, the other holds none and runs in a browser.

Then give it access to the Foundry resource. In the Azure portal, open the Foundry resource,
**Access control (IAM)**, add a role assignment, members **User, group, or service principal**, and
pick the app registration.

| Role | When |
|---|---|
| **Foundry Agent Consumer** | The right answer. Least privilege for a principal that only calls agent endpoints, which is exactly what this plugin does. Measured 13 October 2026: with this role the plugin assesses claims, but the settings page cannot list the project's agents (the Status block says so and carries on) |
| **Foundry User** | Only if you want the settings page to list your agents. It is far broader: it can read the project and the account keys |

The plugin calls the agent on its **own endpoint**
(`.../agents/<name>/endpoint/protocols/openai/responses?api-version=v1`), which is the route Foundry
Agent Consumer permits. The project-level Responses route with an `agent_reference` refuses that role
with 403, which is why version 1.0.0 of this plugin could not run with the role this table
recommends. Fixed in 1.0.1.

Two things Microsoft says explicitly and are easy to get wrong:

- **These roles were renamed.** *Foundry User, Foundry Owner, Foundry Account Owner* and *Foundry
  Project Manager* were previously *Azure AI User, Azure AI Owner, Azure AI Account Owner* and
  *Azure AI Project Manager*. You may still see either set of names. The role IDs did not change.
- **Do not assign anything starting with "Cognitive Services", and do not use "Azure AI
  Developer".** Those apply to AI Services resources and to Machine Learning workspaces
  respectively, not to Foundry projects.
  ([Learn: built-in roles for Microsoft Foundry](https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry#built-in-roles))

You can also assign a role at the scope of **one agent** rather than the whole project, which is
worth doing when the form is public: it grants endpoint access to that agent and nothing else.
([Learn: agent-scope role assignments](https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry#manage-role-assignments))

### 3. Fill in the settings and add the shortcode

Tenant id, client id, secret, Foundry account name, project name and agent name. Then
`[claims365_claim_form]` on a page.

**The account name is the resource name only.** The plugin builds
`https://<name>.services.ai.azure.com/api/projects/<project>` from it. Every value that ends up
in a URL is checked for the shape it must have when you save, and one that is not, such as an
account name with a dot or a slash in it, is refused with a message rather than stored.

The form takes a **JPEG or PNG** of up to 4 MB for each image. The type is read from the file's
bytes, not its name, so a renamed file of any other kind is refused before it reaches Foundry or
Dataverse. It works with any permalink setting, Plain included.

### 4. Optionally, log the claims

Leave the Dataverse organisation URL blank and the form works: it assesses, shows the verdict, and
forgets. Fill it in and every claim is written down before assessment starts, so a claim that fails
assessment still leaves a record with the evidence attached. The columns, the permissions and how
to use your own column names are in [`reference/dataverse-table.md`](reference/dataverse-table.md).

Every write is checked. The verdict card says **Logged to Dataverse** only when the verdict was
actually written, and when something fails the claimant is told what happened to their claim in a
plain sentence, while the detail (which can name your app registration or a missing privilege) goes
to the row's error column and to the PHP error log, not to the page.

**A row left at `running` is worth a look.** The browser polls for about two minutes. If Foundry
has not finished by then, the claimant is told the assessment is taking longer than expected, and
nothing writes a verdict later, so the row stays at `running`. It happened once in testing, on
14 October 2026, on a run Foundry never completed; the same claim resubmitted was assessed in
18 seconds. An operator view that lists rows still `running` after ten minutes catches these.

---

## Customer sign-in, and why it is fiddlier than it looks

Optional, and off unless you fill in an authority and a client id. Off, the widget answers
anonymously and shows no Sign in button at all. On, a visitor signs in with their own account and
the agent can answer about their own records.

**It adds identity; it does not remove the anonymous requirement.** The Direct Line handshake is
anonymous either way, so the agent must still be published for anonymous access. See
[Before you go live](#before-you-go-live).

The flow has three parts and only the first is obvious:

1. **The browser signs the visitor in** with OAuth 2.0 and PKCE against your customer directory,
   and holds the resulting token. A public client registration, no secret. Add the page's own URL
   as a redirect URI.
2. **The agent conversation is signed in separately.** Holding a token on the page proves nothing
   to the agent. When the agent raises an OAuth card, the widget posts the visitor's token to the
   `sasUrl` the card carries, and *that* is what signs the conversation in.
3. **Only an Authenticate node raises that card.** A tool input merely pinned to the signed-in
   user's token does not prompt: it resolves empty and the call behind it fails with a bad-request
   error that names nothing useful. This is why the widget sends a silent priming message
   (`sign me in` by default) straight after sign-in, to make the agent raise its prompt while the
   token is still in hand.

Two safety properties in that flow are deliberate and worth keeping if you rework it. The widget
checks the `sasUrl` host against a Microsoft allow-list before posting a live credential to it, so
an agent returning a third-party URL gets refused rather than handed a token. And signing out tears
down the **conversation**, not just the local copy of the token, because the conversation was
signed in separately and would otherwise keep answering personal questions for someone the panel
says is signed out.

---

## Secrets, and what only looks like one

Getting this wrong in either direction causes problems, so it is worth being exact.

**Genuinely secret. One item.** The client secret on the app registration the *server* uses. It is
used only in PHP, it never appears in a REST response, and the settings page never renders it back
into the form: the field shows empty with a "stored" placeholder, and saving it empty keeps what is
already there. Better still, keep it out of the database entirely:

```php
// wp-config.php
define( 'C365A_CLIENT_SECRET', 'the-secret' );
```

That takes precedence over the stored option, so a database dump, a migration or a staging clone
does not carry it.

**Not secret, and cannot be.** The agent schema name, the environment host, and the public sign-in
client id. A browser needs all three to open a conversation, so they are in the page source of
every site that runs a widget like this one. A public OAuth client is public by design, which is
what PKCE exists for.

**What that actually means.** Publishing an agent for anonymous access makes it callable by anyone
who reads your page source, and every call spends metered capacity on your account. Treat
"anonymous" as a decision about **cost and abuse**, not only about convenience. See the next
section.

**Never put a client secret in `standalone/chat-widget.html`.** That file is served to browsers in
full. It has a comment saying so at the point where you would be tempted.

---

## Before you go live

A site on the open internet has four things worth deciding rather than discovering.

**An anonymous agent is a metered endpoint.** Anyone with the schema name can open a Direct Line
conversation, and every conversation spends Copilot Credits on your tenant.

> 🚨 **Customer sign-in is not an alternative to this, and it is worth being blunt about why.**
> The widget opens its Direct Line conversation with an *anonymous* token request. Sign-in then
> layers the visitor's identity on top of a conversation that is already open. So **the agent has
> to be anonymous-callable either way**: turn authentication on at the agent and the widget cannot
> connect at all, signed-in visitor or not.
>
> If your tenant will not permit an anonymous agent, this widget is the wrong shape for you. The
> supported route is the Microsoft 365 Agents SDK or the Copilot Studio client libraries, which
> acquire a token for the user *before* the handshake. That is a different piece of software, not
> a setting on this one.

**The claim form spends your Foundry quota per submission.** The plugin throttles per client IP,
twenty submissions per five minutes, which is a cost control and not a security one. It will not
stop a determined caller and is not trying to. A public form wants a real WAF or a captcha in front
of it, and a spend limit on the Azure resource behind it.

Behind a reverse proxy or a CDN, every visitor arrives from the proxy's address and shares one
allowance. The plugin does not read forwarded headers on its own, because anyone can send one; if
your proxy sets a header you trust, supply the real address through a filter:

```php
add_filter( 'c365a_client_ip', function ( $ip ) {
	return isset( $_SERVER['HTTP_X_REAL_IP'] ) ? $_SERVER['HTTP_X_REAL_IP'] : $ip;   // your proxy's header
} );
```

**Nothing is deleted, ever.** Claims, evidence images and assessments accumulate in Dataverse. If
real people will use this, that is personal data, and retention, access and deletion are decisions
you have to make rather than defaults you can inherit.

**Model output is untrusted input.** The widget never assigns model text to `innerHTML`: every
piece becomes a text node, links are allow-listed to plain http(s), and a markdown image reference
keeps only its alt text so a model cannot cause an outbound request from your page. Keep those
properties if you rewrite the renderer. They are cheap and they are the difference between
rendering markdown and rendering whatever arrives.

---

## Design decisions

**There is no public route that lists your agents.** It would be an inventory of your AI estate
handed to anyone who asks, and it buys a visitor nothing. The settings page reads that list
server-side instead.

**Whose agent runs a claim is not a decision the browser makes.** There is one agent and it comes
from settings, never from the request body. If you ever add a second, resolve it server-side.

**Nor is which row an assessment writes to.** When a claim is submitted, the server remembers its
Dataverse row against the Foundry response id, for an hour, and the poll names only the response.
The browser never sees the row id. Every update also carries `If-Match: *`, because Dataverse treats
a PATCH by key as an upsert: without it, a wrong id would create a row instead of failing.

**The claimant's words are data.** The policy number and the description of what happened go to
the agent after its instruction, between markers that say they are information to assess, so a
description written as an instruction is evidence about the claim rather than a change to the
task.

---

## The traps that are already handled

These are in the code with comments explaining them. They are listed here so you recognise them if
you rewrite any of it, because each one cost a real debugging session.

| Where | The trap |
|---|---|
| `assistant.js` | **No `&&` operator anywhere in the file.** WordPress rewrites it to an HTML entity when script is saved into *page content*, and the widget dies with a bare "Invalid or unexpected token". Nested `if` reads fine |
| `assistant.js` | Direct Line **rewrites `from.id`** on your own activities, so an echo cannot be filtered by the id you sent. Only the bot carries role `bot` |
| `assistant.js` | The end of a turn is inferred from **silence**. The protocol has no last-message marker and a long answer arrives as several activities |
| `assistant.js` | The Direct Line base URL is **regional** and comes from the environment's `regionalchannelsettings`. The public default is a fallback, not the answer |
| `assistant.js` | A conversation and its token **expire**, after about an hour. A message the expired conversation refuses opens a fresh one and is sent again, rather than every later question getting no answer |
| `assistant.js` | The transcript uses **asterisk emphasis only**. An `_italic_` rule turns `cp_policy and cp_claim` into italics, and agents talk about column names constantly |
| `claim-form.php` | The Agent Service token audience is **`https://ai.azure.com`**, not `cognitiveservices.azure.com`. The wrong one returns 401 naming the audience it wanted |
| `claim-form.php` | **Two agent APIs return different agents.** `/agents` is what the portal builds and there the id *is* the name; `/assistants` is the older surface with `asst_...` ids. The plugin runs portal agents, so only `/agents` is listed |
| `claim-form.php` | The agent is run on **its own endpoint**, not through the project-level Responses route with an `agent_reference`: the least-privilege role answers 403 on the latter and 200 on the former (13 October 2026) |
| `claim-form.php` | Assessment is **two phase**. It measured 11 to 40 seconds, which is longer than a shared host will hold a PHP request open |
| both | **CDN caching.** A CDN in front of the site cached REST responses and answered HIT without consulting the origin, so one visitor's poll result could be served to the next. The origin marks the namespace no-store *and* every call carries a unique query string, because an origin header cannot evict an entry a CDN never revalidates |

There is one more that belongs to Copilot Studio rather than to this code, and it is the most
useful thing to know before you start:

> **Grounded is not the same as correct.** Asked for a settlement figure, the reference agent
> confidently cited a number that was wrong, from a record that contained the right one. The
> mechanism is query-dependent retrieval: a question about money returns the money field, and the
> sentence holding the actual answer never reaches the model. It is not a hallucination and a
> citation does not catch it.
>
> The fix is architectural. Put anything that must be right behind a **typed tool** rather than
> behind retrieval, which is the same conclusion the anonymous-access constraint forces on you
> anyway.

---

## The standalone widget

`standalone/chat-widget.html` is the same widget with no WordPress at all: one file holding the
markup, the styles and the script, with a config block at the top to fill in. Paste it into a page,
a template, or anything that renders HTML.

It is not a second implementation. It is assembled from the plugin's own `assistant.css` and
`assistant.js` by `build_standalone.py`, so if you change either, re-run it:

```bash
cd standalone
python build_standalone.py
```

The markup lives in the builder rather than in a shared file, because the plugin's copy is a PHP
template and this one is static. If you change the ids in one, change them in the other.

---

## Reskinning it

Both stylesheets put every colour in a token block at the top and use nothing else below, so
replacing those values re-skins the whole thing. One convention is worth keeping rather than
overwriting:

**Ochre marks exactly one thing: that a visitor is signed in.** Spend the accent colour on a chip,
a border and a heading as well and it stops meaning anything, and the one state a visitor needs to
notice becomes just another decorated element.

The widget also carries a few behaviours that look like polish and are not. The panel minimises
rather than closes, so a conversation survives being put away. Reduced motion is treated as a
different design rather than a broken one: travel and scale are dropped, but the two progress
signals keep moving, because a reader with no sign that anything is happening is worse off than one
seeing a small animation. And the transcript never smooth-scrolls, because a smooth scroll is still
travelling when the next activity lands, which breaks the "is the reader at the bottom" test and
kills auto-follow for the rest of a multi-part answer.

---

## What has been run, and what has not

It has been installed and exercised on a real WordPress, PHP 8.5 and WordPress 7.0: the plugin
activates cleanly, both shortcodes render on a page with no PHP warnings, an anonymous visitor sees
nothing at all until you configure it, the REST routes refuse politely while unconfigured, the file
gates hold, the settings page never renders your stored secret back into itself, and no server-side
value reaches the browser.

**The claim form was run end to end on 13 October 2026** with the roles this README names: a
confidential app registration holding Foundry Agent Consumer on the Foundry resource and a Dataverse
application user with a custom role holding Create, Read and Write on the table at the User level.
That run is what found the route problem fixed in 1.0.1. The chat widget's live paths (a Direct Line
conversation and the customer sign-in round trip): the anonymous conversation ran on 10 October 2026
from `standalone/chat-widget.html`, which carries the same script as the plugin, against a published
agent; the customer sign-in round trip was not re-run for the book.

**Version 1.0.2 was run on 14 October 2026** on the same WordPress and identity: two claims through
the form, one with the reference left blank and one through the `?rest_route=` address that Plain
permalinks use, each assessed and written to Dataverse with both files attached, and the browser
receiving only the fields the card shows. Sent directly to the REST routes, a poll for an assessment
the site did not start, with or without a row id, answered 404, and a text file named `.jpg` was
refused as not a JPEG or PNG. The widget's reconnect was run from the standalone page against a
published agent, with one message post refused to stand in for an expired conversation: it opened a
new conversation, sent the question again and showed the answer. Not run: saving the settings page
with a malformed value (the checks were tested against sample values instead).

Expect your first hour to go on the three configuration steps rather than on the code.

## Licence and provenance

GPL-2.0-or-later, matching WordPress.

This is a worked example for the book. Read it, take what is useful, and own what you deploy. If one file earns
its keep, it is `assistant.js`, and the reason is the comments rather than the code.
