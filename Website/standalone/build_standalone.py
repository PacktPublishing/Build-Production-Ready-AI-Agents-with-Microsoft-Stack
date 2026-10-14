#!/usr/bin/env python3
"""Assemble chat-widget.html from the plugin's own CSS and JS.

The standalone widget is not a second implementation. It is the plugin's assistant.css and
assistant.js, unchanged, wrapped in the markup that assistant.php renders and a CONFIG block
where WordPress would have localised the settings.

Run this after editing either asset, or the two copies drift:

    python build_standalone.py

Nothing is fetched, nothing is minified, and no credential is read: the CONFIG block is
emitted with empty values for somebody to fill in.
"""
from __future__ import annotations

import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, os.pardir, "claims365-assistant", "assets")
OUT = os.path.join(HERE, "chat-widget.html")

# The markup assistant.php renders, with the templated values written out as defaults. Keep it
# in step with includes/assistant.php: the ids are what assistant.js binds to.
MARKUP = """  <button type="button" id="ci-launch"
          aria-expanded="false" aria-haspopup="dialog" aria-controls="ci-panel">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
         stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <path d="M21 11.5a8.4 8.4 0 0 1-9 8.4 9.9 9.9 0 0 1-2.8-.4L3 21l1.6-4.7A8.2 8.2 0 0 1 3.6 11.5a8.4 8.4 0 0 1 9-8.4 8.4 8.4 0 0 1 8.4 8.4z"/>
    </svg>
    <span>Ask us</span>
    <span class="ci-sr" id="ci-launchsr"> - the assistant</span>
    <span class="ci-dot" aria-hidden="true"></span>
  </button>

  <div id="ci-panel" hidden role="dialog" aria-labelledby="ci-title">
    <div id="ci-head">
      <div id="ci-avatar" aria-hidden="true">
        <svg id="ci-face" viewBox="0 0 40 40" aria-hidden="true" focusable="false">
          <g class="cif-body">
            <path class="cif-ink" d="M4 35v-1.7c0-5.1 6-8.3 13.5-8.3S31 28.2 31 33.3V35z"/>
            <circle class="cif-ink" cx="17.5" cy="15.5" r="8"/>
            <g class="cif-eyes">
              <circle class="cif-eye" cx="14.7" cy="15" r="1.75"/>
              <circle class="cif-eye" cx="20.3" cy="15" r="1.75"/>
            </g>
            <path class="cif-cut" d="M14.5 19.2a4.1 4.1 0 0 0 6 0"/>
          </g>
          <g class="cif-hand">
            <rect class="cif-ink" x="30.7" y="17.4" width="4" height="6.8" rx="2"/>
            <circle class="cif-ink" cx="29.6" cy="15.6" r="2"/>
            <rect class="cif-ink" x="29.4" y="10.6" width="7.4" height="7.8" rx="3.3"/>
          </g>
          <g class="cif-think">
            <circle class="cif-ink" cx="29.6" cy="8.4" r="2"/>
            <circle class="cif-ink" cx="34.4" cy="4.6" r="2.7"/>
            <circle class="cif-ink" cx="37.4" cy="2.4" r="1.5"/>
          </g>
          <g class="cif-spark"><path class="cif-ink" d="M30.6 3.2 32.4 7.4 36.6 9.2 32.4 11 30.6 15.2 28.8 11 24.6 9.2 28.8 7.4z"/></g>
        </svg>
      </div>
      <div>
        <h2 id="ci-title">Assistant</h2>
        <div id="ci-status">
          <span class="ci-pip" aria-hidden="true"></span>
          <span id="ci-statust">Ready</span>
        </div>
      </div>
      <button type="button" id="ci-min" aria-label="Minimise the assistant" title="Minimise">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"
             stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M6 9l6 6 6-6"/>
        </svg>
      </button>
      <span id="ci-arch" hidden aria-hidden="true"><svg viewBox="0 0 24 24" fill="none"
           stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true">
        <path d="M5 21V11a7 7 0 0 1 14 0v10"/><circle cx="12" cy="13.4" r="2.1" fill="currentColor" stroke="none"/>
      </svg></span>
    </div>

    <div id="ci-log" role="log" aria-live="polite" aria-label="Conversation" tabindex="0"></div>

    <div id="ci-chips" role="group" aria-label="Suggested questions">
      <!-- Up to four. Delete them all and the strip hides itself. -->
      <button type="button" class="ci-chip">What does my cover include?</button>
      <button type="button" class="ci-chip">How long does a claim take?</button>
    </div>

    <div id="ci-foot">
      <div id="ci-row">
        <label for="ci-q" class="ci-sr">Your message</label>
        <input type="text" id="ci-q" placeholder="Type your question" autocomplete="off"
               enterkeyhint="send" autocapitalize="sentences" spellcheck="false">
        <button type="button" id="ci-send" aria-label="Send message">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
               stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M22 2 11 13"/><path d="M22 2l-7 20-4-9-9-4 20-7z"/>
          </svg>
        </button>
      </div>
      <!-- Delete this whole block if you are not using customer sign-in. -->
      <div id="ci-auth">
        <span id="ci-authnote">Sign in to ask about your own records</span>
        <span>
          <button type="button" id="ci-in">Sign in</button>
          <button type="button" id="ci-out" class="ci-quiet" hidden>Sign out</button>
        </span>
      </div>
    </div>
  </div>

  <p id="ci-live" class="ci-sr" role="status" aria-live="polite"></p>
  <p id="ci-alert" class="ci-sr" role="alert"></p>"""

CONFIG = """<script>
/* ===========================================================================
   FILL THESE IN. Nothing else in this file needs changing.

   envHost     Your Power Platform environment host. Take the environment GUID, strip the
               hyphens, put a dot before the last two characters, and append
               .environment.api.powerplatform.com
   schema      Your agent's schema name: Copilot Studio, Settings, Advanced, Metadata.
   greeting    The first line the panel says.

   signin      OPTIONAL. Set it to null (the default) for an anonymous-only widget, with no
               Sign in button. To switch it on, delete the `null` and uncomment the object
               below - and delete the #ci-auth block in the markup above if you leave it off.

   NOTHING HERE IS A SECRET, and it cannot be: a browser needs all of it to open a
   conversation, so it is readable in the page source of any site that runs a widget like
   this. The sign-in client id is a PUBLIC client using PKCE, which is exactly the kind of
   OAuth client that is designed to be published. Never put a client SECRET in this file.
   =========================================================================== */
window.C365A = {
  envHost:  "",
  schema:   "",
  apiVersion: "api-version=2022-03-01-preview",
  greeting: "Hello. How can I help?",
  signin:   null
  /*
  signin: {
    authority: "https://<your-tenant>.ciamlogin.com/<tenant-guid>",
    clientId:  "<public SPA app registration id>",
    scopes:    "openid profile email offline_access",
    prime:     "sign me in"
  }
  */
};
</script>"""


def main() -> int:
    css = io.open(os.path.join(ASSETS, "assistant.css"), encoding="utf-8").read()
    js = io.open(os.path.join(ASSETS, "assistant.js"), encoding="utf-8").read()

    # A file with a real secret in it is not something to write quietly.
    for needle in ("client_secret", "clientSecret"):
        if needle in js:
            print("REFUSING: %s appears in assistant.js" % needle, file=sys.stderr)
            return 1

    html = (
        "<!-- Claims 365 assistant - standalone.\n"
        "     Assembled by build_standalone.py from the plugin's assistant.css and\n"
        "     assistant.js. Edit those and re-run rather than editing this file.\n"
        "\n"
        "     Paste this whole block into a page. It is self-contained: no build step, no\n"
        "     framework, no external script, and one fetch to Microsoft when somebody asks a\n"
        "     question. -->\n"
        '<div id="ci-assistant">\n'
        "  <style>\n" + css + "\n  </style>\n\n"
        + MARKUP + "\n"
        "</div>\n\n"
        + CONFIG + "\n"
        "<script>\n" + js + "</script>\n"
    )
    io.open(OUT, "w", encoding="utf-8", newline="\n").write(html)
    print("wrote %s (%d bytes)" % (os.path.relpath(OUT, HERE), len(html.encode("utf-8"))))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
