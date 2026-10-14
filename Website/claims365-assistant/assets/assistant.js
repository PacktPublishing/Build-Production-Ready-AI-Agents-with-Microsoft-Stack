/* Claims 365 assistant - the chat widget.
 *
 * Anonymous Direct Line, so anything the agent is grounded on answers with no account at all.
 * If customer sign-in is configured, signing in uses the visitor's OWN account: the page hands
 * their token to the agent through the OAuth card's sasUrl, and the agent passes it on to
 * whatever it calls as a PINNED input the model cannot override.
 *
 * ---------------------------------------------------------------------------------------
 * FOUR MEASURED TRAPS. All four are encoded below; do not "simplify" them back out.
 *
 *   1. NO DOUBLE-AMPERSAND OPERATOR ANYWHERE IN THIS FILE, not even in a comment. WordPress
 *      rewrites two ampersands to an HTML entity when script is saved into PAGE CONTENT, and
 *      the whole widget then dies with a bare "Invalid or unexpected token". This file is
 *      served as a real asset so it would be safe here; the rule is kept anyway, so the code
 *      can be pasted into a page block or into standalone/chat-widget.html without a rewrite.
 *      Nested `if` reads fine, and is what every conditional below uses.
 *
 *   2. Direct Line REWRITES from.id on your own activities, so an echo of your own message
 *      cannot be filtered by the id you sent. Only the bot carries role "bot". Filter on that.
 *
 *   3. Only an OAuthInput node demands authentication. An input merely pinned to
 *      System.User.AccessToken does NOT prompt: it resolves empty, and the call behind it
 *      fails with a bad-request. That is what the silent priming message below is for.
 *
 *   4. The end of a turn is inferred from SILENCE. The protocol has no "last message" marker,
 *      and a multi-part answer arrives as several activities.
 *
 * No credential is present in this file. The schema name, the environment host and the public
 * sign-in client id are all values a browser must have to work at all.
 */
(function () {
  var C = window.C365A || {};
  var CFG = {
    schema: C.schema || "",
    apiv:   C.apiVersion || "api-version=2022-03-01-preview",
    greeting: C.greeting || "Hello. How can I help?"
  };
  var AUTH = C.signin || null;           /* null when customer sign-in is not configured */

  var $ = function (id) { return document.getElementById(id); };
  if (!$("ci-assistant")) { return; }

  var dl = null, conv = null, ctok = null, wm = null, sas = null;
  var custToken = null, custName = null, greeted = false, ready = false, authPassed = false;

  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  /* ================= transcript rendering =================
     Copilot Studio answers in MARKDOWN, so "**2,700**" reaches the reader as literal asterisks
     unless something renders it. It is also MODEL OUTPUT, which means untrusted: nothing below
     ever assigns innerHTML from it. Every piece of model text becomes a text node and every
     element is created explicitly, so a payload never meets an HTML parser, in any context.
     Our own strings take the plain path and are never parsed as markdown at all.

     Allow-list, not deny-list, on links: anything that is not plainly http(s) stays inert text,
     so javascript:, data:, vbscript: and the encoding tricks around them fail closed. */
  var HTTP = /^https?:\/\/[^\s"'<>]+$/i;
  /* Asterisk emphasis only. UNDERSCORE EMPHASIS IS DELIBERATELY ABSENT: an agent that talks
     about table or column names writes "cp_policy and cp_claim" as ordinary prose, and an
     `_italic_` rule turns everything between the two underscores into italics. Copilot Studio
     emits asterisks anyway. */
  var INLINE = /(`[^`\n]+`)|(!\[[^\]\n]*\]\([^)\s]*\))|(\[[^\]\n]*\]\([^)\s]*\))|(\*\*(?=\S)[^\n]*?\S\*\*)|(\*(?=[^\s*])[^*\n]*[^\s*]\*)|(https?:\/\/[^\s<>()\[\]"']+)/;

  function linkNode(label, url) {
    var u = String(url === undefined || url === null ? "" : url).trim();
    if (!HTTP.test(u)) { return document.createTextNode(label); }
    var a = document.createElement("a");
    a.href = u;                    /* a property, never a hand-built attribute string */
    a.textContent = label;
    a.target = "_blank";
    /* Show where it REALLY goes, taken from the parsed anchor rather than the model's raw
       string, so a label that reads like one destination cannot hide another. */
    try { a.title = a.hostname + " (link written by the assistant)"; } catch (e) {}
    /* noreferrer is a security control here rather than boilerplate: a link the MODEL chose
       must not hand the destination your URL, and nofollow/ugc say plainly that this is
       generated content. */
    a.rel = "noopener noreferrer nofollow ugc";
    return a;
  }
  function wrapInline(tag, text, depth) {
    var el = document.createElement(tag);
    appendInline(el, text, depth + 1);
    return el;
  }
  function appendInline(host, text, depth) {
    if (depth > 3) { host.appendChild(document.createTextNode(text)); return; }
    var re = new RegExp(INLINE.source, "g");
    var last = 0;
    var m = re.exec(text);
    while (m !== null) {
      if (m.index > last) { host.appendChild(document.createTextNode(text.slice(last, m.index))); }
      var tok = m[0];
      if (m[1]) {                        /* `code`, whose content stays inert */
        var c = document.createElement("code");
        c.textContent = tok.slice(1, -1);
        host.appendChild(c);
      } else if (m[2]) {                 /* ![alt](url) - keep the ALT TEXT, drop the rest */
        /* An image reference chosen by the model would be an outbound request from a public
           page to a URL nobody vetted, i.e. a tracking pixel with extra steps. Only the label
           survives, as plain text. Without this arm the '!' is left behind and the URL still
           becomes a clickable link. */
        host.appendChild(document.createTextNode(tok.slice(2, tok.indexOf("]("))));
      } else if (m[3]) {                 /* [label](url) */
        var cut = tok.indexOf("](");
        host.appendChild(linkNode(tok.slice(1, cut), tok.slice(cut + 2, -1)));
      } else if (m[4]) {
        host.appendChild(wrapInline("strong", tok.slice(2, -2), depth));
      } else if (m[5]) {
        host.appendChild(wrapInline("em", tok.slice(1, -1), depth));
      } else {
        /* a bare url swallows the sentence punctuation after it: give that back */
        var raw = tok, trail = "";
        var mt = /[.,;:!?]+$/.exec(raw);
        if (mt) { trail = mt[0]; raw = raw.slice(0, raw.length - trail.length); }
        host.appendChild(linkNode(raw, raw));
        if (trail !== "") { host.appendChild(document.createTextNode(trail)); }
      }
      last = m.index + tok.length;
      m = re.exec(text);
    }
    if (last < text.length) { host.appendChild(document.createTextNode(text.slice(last))); }
  }
  /* Blocks: paragraphs, bullet and numbered lists. A markdown heading is flattened to emphasis
     on purpose - a chat bubble must not inject h1..h6 into the page outline. */
  function renderRich(host, text) {
    /* Normalise first: line endings, the space-like characters that break a bullet match, and
       the invisible controls (zero-width, bidi overrides) that can make a link's text disagree
       with where it goes. Tab and newline are deliberately kept. */
    var src = String(text === undefined || text === null ? "" : text)
      .replace(/\r\n?/g, "\n")
      .replace(/[\u00a0\u2007\u202f]/g, " ")
      .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u200b-\u200f\u2028\u2029\u202a-\u202e\u2066-\u2069]/g, "");
    var lines = src.split("\n");
    var buf = [];
    var list = null;
    function flushPara() {
      if (buf.length === 0) { return; }
      var p = document.createElement("p");
      for (var i = 0; i < buf.length; i++) {
        if (i > 0) { p.appendChild(document.createElement("br")); }
        appendInline(p, buf[i], 0);
      }
      host.appendChild(p);
      buf = [];
    }
    function flushList() {
      if (list === null) { return; }
      host.appendChild(list);
      list = null;
    }
    function intoList(tag, itemText) {
      flushPara();
      if (list !== null) { if (list.tagName !== tag) { flushList(); } }
      if (list === null) { list = document.createElement(tag); }
      var li = document.createElement("li");
      appendInline(li, itemText, 0);
      list.appendChild(li);
    }
    for (var n = 0; n < lines.length; n++) {
      var line = lines[n];
      if (/^\s*$/.test(line)) { flushPara(); flushList(); continue; }
      var mh = /^\s*#{1,6}\s+(.*)$/.exec(line);
      if (mh) {
        flushPara(); flushList();
        var hp = document.createElement("p");
        hp.className = "ci-h";
        hp.appendChild(wrapInline("strong", mh[1], 0));
        host.appendChild(hp);
        continue;
      }
      var mb = /^\s*[-\u2022]\s+(.*)$/.exec(line);
      if (mb === null) { mb = /^\s*\*\s+(.*)$/.exec(line); }
      if (mb) { intoList("UL", mb[1]); continue; }
      var mo = /^\s*\d{1,3}[.)]\s+(.*)$/.exec(line);
      if (mo) { intoList("OL", mo[1]); continue; }
      flushList();
      buf.push(line);
    }
    flushPara(); flushList();
    if (host.childNodes.length === 0) { host.appendChild(document.createTextNode(src)); }
    return host;
  }

  /* ================= the log ================= */
  function archNode() {
    /* Clone the brand mark rather than parsing any markup. It lives in its own hidden node,
       NOT in #ci-avatar: the header avatar is an animated face, and cloning that into every
       bot row would put a dozen animating characters in the transcript and change what a
       message avatar means. The arch marks a message; the face marks the assistant. */
    var src = $("ci-arch");
    src = src ? src.firstElementChild : null;
    return src ? src.cloneNode(true) : document.createElement("span");
  }
  function nearBottom() {
    var l = $("ci-log");
    return l.scrollHeight - l.scrollTop - l.clientHeight < 90;
  }
  function toBottom() {
    var l = $("ci-log");
    l.scrollTop = l.scrollHeight;
  }
  function addRow(row, animate) {
    var log = $("ci-log");
    var stick = nearBottom();          /* decided BEFORE the row changes the height */
    if (animate) { row.className = row.className + " ci-pre"; }
    log.appendChild(row);
    if (animate) {
      void row.offsetWidth;            /* one reflow, so removing the class transitions */
      row.classList.remove("ci-pre");
    }
    /* Never yank a reader who has scrolled up to re-read an answer. */
    if (stick) { toBottom(); }
    return row;
  }
  function srSays(label) {
    var s = document.createElement("span");
    s.className = "ci-sr";
    s.textContent = label + ": ";
    return s;
  }

  var hist = [];
  function remember(w, t) {
    hist.push({ w: w, t: t });
    if (hist.length > 40) { hist = hist.slice(hist.length - 40); }
    try { sessionStorage.setItem("ci_hist", JSON.stringify(hist)); } catch (e) {}
  }

  /* who: "me" or "bot". Bot text is rendered as markdown; the reader's own words are not. */
  function paint_msg(who, text, animate) {
    var log = $("ci-log");
    var row = document.createElement("div");
    var mine = who === "me";
    row.className = "ci-row " + (mine ? "ci-row-me" : "ci-row-bot");
    if (!mine) {
      var prev = log.lastElementChild;
      if (prev) {
        if (prev.className.indexOf("ci-row-bot") >= 0) { row.className = row.className + " ci-cont"; }
      }
      var av = document.createElement("div");
      av.className = "ci-av";
      av.setAttribute("aria-hidden", "true");
      av.appendChild(archNode());
      row.appendChild(av);
    }
    var b = document.createElement("div");
    b.className = "ci-b " + (mine ? "ci-me ci-plain" : "ci-bot");
    b.appendChild(srSays(mine ? "You" : "Assistant"));
    if (mine) {
      b.appendChild(document.createTextNode(text));
    } else {
      /* Rendered into a detached node first, so a parser bug can never leave half an answer on
         screen: on any failure the reader still gets the words, verbatim. */
      var frag = document.createElement("div");
      try {
        renderRich(frag, text);
      } catch (e) {
        frag.textContent = "";
        frag.appendChild(document.createTextNode(text));
        b.className = b.className + " ci-plain";
      }
      while (frag.firstChild) { b.appendChild(frag.firstChild); }
    }
    row.appendChild(b);
    addRow(row, animate);
    return b;
  }
  function say(who, text) {
    var b = paint_msg(who, text, true);
    remember(who, text);
    if (who !== "me") {
      if (!panelOpen) { unread(true); }
    }
    return b;
  }
  /* Anything the WIDGET says - sign-in state, failures - is dressed as a notice, never as
     something the assistant claimed. */
  function paint_sys(text, animate) {
    var row = document.createElement("div");
    row.className = "ci-row ci-row-sys";
    var d = document.createElement("div");
    d.className = "ci-sys";
    d.appendChild(document.createTextNode(text));
    row.appendChild(d);
    addRow(row, animate);
    return d;
  }
  function sys(text) {
    remember("sys", text);
    return paint_sys(text, true);
  }
  function fail(text) {
    /* role="alert" carries it once, assertively. The visible notice is then marked aria-hidden
       so the transcript's own live region does not read the same sentence a second time.
       Cleared on the next turn so a repeat failure announces again. */
    $("ci-alert").textContent = text;
    var row = sys(text);
    var host = row.parentNode;
    if (host) { host.setAttribute("aria-hidden", "true"); }
    return row;
  }

  function thinking(on) {
    faceThink(on);                       /* the face waits with the reader, then nods */
    var old = document.getElementById("ci-typing");
    if (old) { old.remove(); }
    $("ci-live").textContent = on ? "Assistant is typing" : "";
    if (!on) { return; }
    var row = document.createElement("div");
    row.className = "ci-row ci-row-bot";
    row.id = "ci-typing";
    row.setAttribute("aria-hidden", "true");   /* decorative: the status region announces it */
    var av = document.createElement("div");
    av.className = "ci-av";
    av.appendChild(archNode());
    row.appendChild(av);
    var b = document.createElement("div");
    b.className = "ci-b ci-bot ci-dots";
    b.appendChild(document.createElement("i"));
    b.appendChild(document.createElement("i"));
    b.appendChild(document.createElement("i"));
    row.appendChild(b);
    addRow(row, true);
  }

  /* The composer is locked for the length of a turn: a second question mid-flight would share
     the watermark and swallow the first answer. */
  var sending = false;
  function busy(on) {
    sending = on;
    /* aria-disabled, NOT the native disabled attribute. A control that goes disabled loses
       focus to <body>, so a keyboard user would have their place thrown back to the top of the
       document for as long as a 45 second turn. The composer also stays typable, so the next
       question can be written while the last answer is still arriving - ask() is what refuses
       to send two turns at once. */
    $("ci-send").setAttribute("aria-disabled", on ? "true" : "false");
    if (on) { $("ci-send").classList.add("ci-busy"); }
    else { $("ci-send").classList.remove("ci-busy"); }
    /* NO aria-busy on the transcript. Holding aria-busy="true" across a turn is exactly the
       window in which the answer arrives, and a live region marked busy may swallow the
       announcement altogether - so the one reader who depends on it hears nothing. The waiting
       state is announced by #ci-live instead. */
    Array.prototype.forEach.call(document.querySelectorAll("#ci-assistant .ci-chip"),
      function (c) { c.setAttribute("aria-disabled", on ? "true" : "false"); });
  }

  /* Closing MINIMISES: transcript and conversation id are kept, so reopening resumes rather
     than restarting. `hidden` is set only AFTER the close animation, through one funnel that
     both the transitionend and an unconditional timer call - a transition that never fires (a
     background tab, an interrupted close) must not leave the panel visible to a reader or,
     worse, invisible but still focusable.

     Tracked in a variable rather than read back off the hidden attribute: during the close
     animation the panel is still visible but on its way out, and "is it open" has to answer no. */
  var panelOpen = false;
  var closeTimer = null;
  function settleClosed() {
    if (closeTimer !== null) { clearTimeout(closeTimer); closeTimer = null; }
    var p = $("ci-panel");
    if (p.classList.contains("ci-shown")) { return; }   /* reopened mid-close: leave it open */
    p.hidden = true;
  }

  /* ================= the assistant's idle life =================
     The face in the header does one small thing every few seconds: blink, nod, glance, wave,
     think, or pop a hello. The constraints are the interesting part.

       - It runs only while the panel is OPEN and the tab is VISIBLE. A loop ticking behind a
         hidden tab is a cost nobody can see.
       - prefers-reduced-motion stops it dead rather than collapsing it to 1ms like the rest of
         the widget. Everything else here is feedback and has to survive at any speed; this is
         decoration, and a decoration stuttering once a second is worse than still.
       - It never plays the same act twice running, which is most of what separates a character
         from a GIF.
       - While the agent is genuinely thinking, the face thinks too, and it nods when the answer
         lands. The one moment the reader is waiting is the one moment this motion carries
         information rather than personality.
       - Every act is transform and opacity only, so none of it triggers layout.

     Delete the <svg> in the shortcode and every function below turns into a no-op. */
  var FACE_ACTS = [
    { c: "cif-nod", ms: 820 },
    { c: "cif-glance", ms: 1300 },
    { c: "cif-wave", ms: 1200 },
    { c: "cif-think", ms: 1540 },
    { c: "cif-hi", ms: 970 }
  ];
  var FACE_ALL = ["cif-blink", "cif-nod", "cif-glance", "cif-wave", "cif-think", "cif-hi"];
  var faceTimer = null, faceEnd = null, faceLast = "", faceBusy = false;

  function faceCalm() {
    try { return window.matchMedia("(prefers-reduced-motion: reduce)").matches; }
    catch (e) { return false; }
  }
  function faceOff() {
    var a = $("ci-avatar");
    if (!a) { return; }
    var i;
    for (i = 0; i < FACE_ALL.length; i++) { a.classList.remove(FACE_ALL[i]); }
  }
  function facePlay(cls, ms) {
    var a = $("ci-avatar");
    if (!a) { return; }
    if (faceCalm()) { return; }
    faceOff();
    void a.offsetWidth;                /* reflow, so the same act can repeat and still play */
    a.classList.add(cls);
    if (faceEnd !== null) { clearTimeout(faceEnd); }
    /* The class is pulled after the run instead of being left on. A resting face carries no
       animation at all, so a later act always starts from the same known state. */
    faceEnd = setTimeout(function () { faceOff(); faceEnd = null; }, ms);
  }
  function faceAwake() {
    if (faceCalm()) { return false; }
    if (!panelOpen) { return false; }
    if (document.hidden) { return false; }
    return true;
  }
  function faceSoon(lo, hi) {
    if (faceTimer !== null) { clearTimeout(faceTimer); }
    faceTimer = setTimeout(faceTick, lo + Math.random() * (hi - lo));
  }
  function faceTick() {
    faceTimer = null;
    /* No self-reschedule when asleep: open_() and visibilitychange own the restart, so a closed
       panel leaves nothing running. */
    if (!faceAwake()) { return; }
    if (faceBusy) { facePlay("cif-think", 1540); faceSoon(1650, 1950); return; }
    /* A blink is punctuation between the bigger acts, so it never follows another blink.
       Measured before this guard: three blinks inside three seconds, which reads as a twitch
       rather than a person. */
    if (faceLast !== "cif-blink") {
      if (Math.random() < 0.4) {
        faceLast = "cif-blink";
        facePlay("cif-blink", 280);
        faceSoon(1400, 2800);
        return;
      }
    }
    var pick = FACE_ACTS[Math.floor(Math.random() * FACE_ACTS.length)];
    var guard = 0;
    while (pick.c === faceLast) {
      if (guard > 6) { break; }
      pick = FACE_ACTS[Math.floor(Math.random() * FACE_ACTS.length)];
      guard = guard + 1;
    }
    faceLast = pick.c;
    facePlay(pick.c, pick.ms);
    faceSoon(2700, 5400);
  }
  function faceStop() {
    if (faceTimer !== null) { clearTimeout(faceTimer); faceTimer = null; }
    if (faceEnd !== null) { clearTimeout(faceEnd); faceEnd = null; }
    faceOff();
  }
  function faceThink(on) {
    faceBusy = on;
    if (!faceAwake()) { return; }
    if (on) { facePlay("cif-think", 1540); faceSoon(1650, 1950); }
    else { facePlay("cif-nod", 820); faceSoon(2600, 4400); }
  }
  document.addEventListener("visibilitychange", function () {
    if (document.hidden) { faceStop(); return; }
    if (panelOpen) { faceSoon(900, 2000); }
  });

  function open_(on) {
    var p = $("ci-panel"), l = $("ci-launch");
    panelOpen = on;
    try { sessionStorage.setItem("ci_open", on ? "1" : "0"); } catch (e) {}
    l.setAttribute("aria-expanded", on ? "true" : "false");
    if (on) {
      if (closeTimer !== null) { clearTimeout(closeTimer); closeTimer = null; }
      p.removeAttribute("inert");
      unread(false);
      l.classList.add("ci-gone");
      setTimeout(function () {
        if (p.classList.contains("ci-shown")) { l.hidden = true; }
      }, 150);
      p.hidden = false;
      void p.offsetWidth;                 /* start from the collapsed state, then transition */
      p.classList.add("ci-shown");
      /* the face says hello with the panel, then settles into its idle loop */
      faceLast = "";
      facePlay("cif-hi", 970);
      faceSoon(1500, 2700);
      if (!greeted) { greeted = true; greet(); }
      toBottom();                        /* reopening lands on the newest reply, not where you left */
      setTimeout(function () {
        try { $("ci-q").focus({ preventScroll: true }); } catch (e) { $("ci-q").focus(); }
      }, 120);
    } else {
      p.classList.remove("ci-shown");
      p.setAttribute("inert", "");
      faceStop();                        /* nothing keeps ticking behind a closed panel */
      l.hidden = false;
      void l.offsetWidth;
      l.classList.remove("ci-gone");
      closeTimer = setTimeout(settleClosed, 420);
      try { l.focus({ preventScroll: true }); } catch (e) { l.focus(); }
    }
  }

  function greet() {
    /* a short typing beat before the first line: the assistant reads as present rather than as
       a block of text that was always there */
    thinking(true);
    setTimeout(function () {
      thinking(false);
      say("bot", CFG.greeting);
      chipsIntoLog();                          /* the suggestions follow the greeting */
      toBottom();
    }, 480);
  }

  /* The suggestions live in the transcript, so they scroll away with the greeting on their own.
     They are NOT hidden after the first question: keeping them costs nothing once they have
     scrolled up. `.ci-settled` stops the stagger replaying every time the panel is shown again -
     display:none to display:flex restarts CSS animations. */
  function chipsIntoLog() {
    var c = $("ci-chips");
    if (!c) { return; }
    if (!c.children.length) { c.hidden = true; return; }
    c.className = "ci-inlog";
    $("ci-log").appendChild(c);
    setTimeout(function () { c.className = "ci-inlog ci-settled"; }, 500);
  }

  /* ================= transport ================= */

  function b64url(buf) {
    return btoa(String.fromCharCode.apply(null, new Uint8Array(buf)))
      .replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }
  function redirectUri() { return location.origin + location.pathname; }
  function saveConv() {
    try {
      sessionStorage.setItem("ci_conv", JSON.stringify(
        { dl: dl, conv: conv, ctok: ctok, wm: wm, sas: sas }));
    } catch (e) {}
  }

  async function jget(u, tok) {
    var h = {}; if (tok) h.Authorization = "Bearer " + tok;
    try {
      var r = await fetch(u, { headers: h });
      return r.ok ? r.json() : null;
    } catch (e) { return null; }
  }
  async function jpost(u, body, tok) {
    var h = { "Content-Type": "application/json" };
    if (tok) h.Authorization = "Bearer " + tok;
    try {
      var r = await fetch(u, { method: "POST", headers: h, body: JSON.stringify(body || {}) });
      if (!r.ok) return null;
      try { return await r.json(); } catch (e2) { return {}; }
    } catch (e) { return null; }
  }

  /* Both of these land in a URL path, so both are shape-checked even though they come from
     this site's own settings. Cheap, and it turns a mistyped setting into a widget that does
     not open a conversation rather than a malformed request. */
  function activeSchema() {
    if (!/^[A-Za-z0-9_]{1,128}$/.test(CFG.schema)) { return ""; }
    return CFG.schema;
  }
  function activeEnv() {
    var h = C.envHost || "";
    if (!/^[A-Za-z0-9.\-]{10,120}\.environment\.api\.powerplatform\.com$/.test(h)) { return ""; }
    return "https://" + h;
  }

  /* WHERE A CUSTOMER'S TOKEN MAY BE POSTED.
     The OAuth card hands us a URL and we POST the signed-in visitor's access token to it.
     Whatever produced that card therefore chooses the destination, so the host is checked
     against Microsoft's own before the token leaves the browser: an agent that returned a
     third-party sasUrl would otherwise be handed a live credential. The list is deliberately
     generous - it is here to refuse somebody else's domain, not to pin one hostname - so if
     sign-in ever fails with the console warning below, widen it rather than removing it. */
  var TOKEN_POST_HOSTS = /(^|\.)(botframework\.com|powerplatform\.com|microsoft\.com|azure\.com|dynamics\.com)$/i;
  function tokenPostOk(url) {
    if (!url) { return false; }
    var host = "";
    try {
      var parsed = new URL(String(url));
      if (parsed.protocol !== "https:") { return false; }
      host = parsed.hostname;
    } catch (e) { return false; }
    if (TOKEN_POST_HOSTS.test(host)) { return true; }
    try { console.warn("claims365 assistant: refused a token post to " + host); } catch (e2) {}
    return false;
  }

  async function connect() {
    var env = activeEnv(), schema = activeSchema();
    if (!env) { throw new Error("the environment host is not set correctly"); }
    if (!schema) { throw new Error("the agent name is not set correctly"); }
    var t = await jget(env + "/powervirtualagents/botsbyschema/" + schema
                       + "/directline/token?" + CFG.apiv);
    if (!t) throw new Error("the assistant is unavailable");
    /* The Direct Line base URL is REGIONAL and comes from the environment, not from a constant.
       The public default below is the fallback, not the answer. */
    var rcs = await jget(env + "/powervirtualagents/regionalchannelsettings?" + CFG.apiv);
    var baseUrl = ((rcs || {}).channelUrlsById || {}).directline;
    if (!baseUrl) baseUrl = "https://directline.botframework.com/";
    dl = baseUrl.replace(/\/$/, "") + "/v3/directline";
    var c = await jpost(dl + "/conversations", {}, t.token);
    if (!c) throw new Error("the assistant is unavailable");
    conv = c.conversationId; ctok = c.token || t.token; wm = null;
    await jpost(dl + "/conversations/" + conv + "/activities",
      { type: "event", name: "startConversation", from: { id: "web", role: "user" },
        channelData: { postBack: true } }, ctok);
    ready = true; saveConv();
    await pump(6000, false);        /* swallow the platform greeting, we print our own */
  }

  /* Poll one turn. NEVER calls sendRaw - nested pumps used to share the watermark and swallow
     each other's activities, which is why answers went missing. It only collects, renders, and
     passes the visitor's token through an OAuth card if one arrives. */
  async function pump(maxMs, render) {
    var end = Date.now() + maxMs;
    var texts = [];
    var lastText = 0;
    while (Date.now() < end) {
      await sleep(1200);
      var u = dl + "/conversations/" + conv + "/activities";
      if (wm) u = u + "?watermark=" + wm;
      var a = await jget(u, ctok);
      if (!a) continue;
      wm = a.watermark || wm; saveConv();
      var acts = a.activities || [];
      for (var k = 0; k < acts.length; k++) {
        var act = acts[k];
        if ((act.from || {}).role !== "bot") continue;   /* trap 2: only the bot carries "bot" */
        var atts = act.attachments || [];
        for (var j = 0; j < atts.length; j++) {
          if ((atts[j].contentType || "").indexOf("oauth") < 0) continue;
          var cc = atts[j].content || {};
          var url = (cc.tokenPostResource || {}).sasUrl;
          if (tokenPostOk(url)) { sas = url; saveConv(); }
          if (custToken) {
            if (sas) {
              await fetch(sas, {
                method: "POST", headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ token: custToken })
              });
              authPassed = true;
            }
          } else if (AUTH) {
            sys("To look that up I need you to sign in. Use Sign in just below.");
          } else {
            sys("That needs a signed-in account, and this page does not offer sign-in.");
          }
        }
        if (act.text) {
          texts.push(act.text);
          lastText = Date.now();
          if (render) {
            thinking(false);        /* the answer replaces the dots rather than following them */
            say("bot", act.text);
          }
        }
      }
      /* Trap 4: end of turn is inferred from silence. */
      if (texts.length) {
        if (Date.now() - lastText > 3200) break;
      }
    }
    return texts;
  }

  /* Post one message into the conversation. A Direct Line conversation and its token expire
     (the token after about an hour), and an expired one refuses the post. Without this every
     later question would get "no answer", so open a fresh conversation and send once more. */
  async function postMessage(q) {
    var msg = { type: "message", text: q, locale: "en-GB", from: { id: "web", role: "user" } };
    if (await jpost(dl + "/conversations/" + conv + "/activities", msg, ctok)) { return; }
    ready = false;
    await connect();
    if (!(await jpost(dl + "/conversations/" + conv + "/activities", msg, ctok))) {
      throw new Error("the assistant is unavailable");
    }
  }

  async function sendRaw(q, render) {
    authPassed = false;
    await postMessage(q);
    if (render) thinking(true);
    var got = await pump(45000, render);
    if (!got.length) {
      /* The turn was consumed by the sign-in card. Now that the token is in, ask once more. */
      if (authPassed) {
        if (render) { thinking(true); }
        await postMessage(q);
        got = await pump(45000, render);
      }
    }
    if (render) thinking(false);
    return got;
  }

  /* ================= customer sign-in (optional) ================= */

  async function signIn() {
    if (!AUTH) { return; }
    var v = b64url(crypto.getRandomValues(new Uint8Array(32)));
    /* The PKCE verifier MUST survive the round trip. If storage is unavailable - private mode,
       blocked cookies - say so HERE rather than failing after the redirect, where the reader has
       left the site and cannot see any message we might print. */
    try {
      sessionStorage.setItem("ci_v", v);
      sessionStorage.setItem("ci_open", "1");
    } catch (e) {
      throw new Error("this browser is blocking site storage, which sign-in needs. Try a "
                    + "normal window rather than a private one.");
    }
    saveConv();
    var c = b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(v)));
    var p = new URLSearchParams({
      client_id: AUTH.clientId, response_type: "code", redirect_uri: redirectUri(),
      scope: AUTH.scopes, code_challenge: c, code_challenge_method: "S256",
      response_mode: "query"
    });
    location.href = AUTH.authority + "/oauth2/v2.0/authorize?" + p;
  }

  async function finishSignIn(code) {
    if (!AUTH) { return; }
    var body = new URLSearchParams({
      client_id: AUTH.clientId, grant_type: "authorization_code", code: code,
      code_verifier: sessionStorage.getItem("ci_v"), redirect_uri: redirectUri(),
      scope: AUTH.scopes
    });
    var r = await fetch(AUTH.authority + "/oauth2/v2.0/token", {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: body
    });
    var t = await r.json();
    if (!t.access_token) throw new Error(t.error_description || "sign-in failed");
    custToken = t.access_token;
    custName = "your account";
    try {
      var c = JSON.parse(atob(t.id_token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
      custName = c.name || c.preferred_username || "your account";
    } catch (e) {}
    history.replaceState({}, "", location.pathname);
    paint();
    /* Holding a token on the page is not enough - trap 3. The AGENT conversation must be signed
       in too, and only an OAuthInput node prompts for that. Prime it silently. */
    try {
      if (!ready) await connect();
      await sendRaw(AUTH.prime, false);
    } catch (e) {}
    sys("Thanks, you are signed in as " + custName + ". Ask about your own records.");
  }

  function paint() {
    if (!AUTH) { return; }
    var signedIn = custToken ? true : false;
    $("ci-statust").textContent = signedIn ? "Signed in as " + custName : "Not signed in";
    if (signedIn) { $("ci-status").classList.add("ci-on"); }
    else { $("ci-status").classList.remove("ci-on"); }
    $("ci-authnote").textContent = signedIn
      ? "You will only see records on this account"
      : "Sign in to ask about your own records";
    /* Hiding the control that currently holds focus would drop a keyboard user back to the top
       of the document, so hand focus to its replacement first. */
    var active = document.activeElement;
    var losing = signedIn ? $("ci-in") : $("ci-out");
    $("ci-in").hidden = signedIn;
    $("ci-out").hidden = !signedIn;
    if (active === losing) {
      var gaining = signedIn ? $("ci-out") : $("ci-in");
      try { gaining.focus({ preventScroll: true }); } catch (e) { gaining.focus(); }
    }
  }

  /* ================= asking ================= */

  async function ask(text) {
    if (sending) { return; }              /* one turn at a time: see busy() */
    var q = (text !== undefined ? text : $("ci-q").value).trim();
    if (!q) { return; }
    $("ci-q").value = "";
    $("ci-alert").textContent = "";       /* so an identical failure announces again */
    say("me", q);
    busy(true);
    /* The dots go up BEFORE connect(): the first question of a session also pays for the Direct
       Line handshake, and a motionless panel reads as a broken one. */
    thinking(true);
    try {
      if (!ready) { await connect(); }
      var got = await sendRaw(q, true);
      if (!got.length) { sys("I did not get an answer that time. Please try again."); }
    } catch (e) {
      fail("Sorry, I could not reach the assistant just now. " + e.message);
    }
    thinking(false);
    busy(false);
  }

  function unread(on) {
    var l = $("ci-launch");
    if (on) { l.classList.add("ci-unread"); } else { l.classList.remove("ci-unread"); }
    var sr = $("ci-launchsr");
    if (sr) {
      sr.textContent = on ? " - new reply waiting" : " - the assistant";
    }
  }

  /* ================= wiring ================= */

  $("ci-launch").onclick = function () { open_(true); };
  $("ci-min").onclick = function () { open_(false); };
  /* One funnel ends a close, whichever gets there first (see settleClosed). */
  $("ci-panel").addEventListener("transitionend", function (e) {
    if (e.target !== $("ci-panel")) { return; }
    if (e.propertyName !== "opacity") { return; }
    settleClosed();
  });
  document.addEventListener("keydown", function (e) {
    if (e.isComposing) { return; }        /* mid IME composition: Escape belongs to the IME */
    if (e.keyCode === 229) { return; }
    if (e.key !== "Escape") { return; }
    if ($("ci-panel").hidden) { return; }
    open_(false);
  });
  /* No sign-in configured means no sign-in ROW. The WordPress shortcode already omits it, but
     the standalone file ships the markup with a "delete this if you are not using it" comment,
     and a Sign in button that does nothing when clicked is worse than no button. Hide it here so
     the two paths cannot disagree. */
  if (!AUTH) {
    var authRow = $("ci-auth");
    if (authRow) { authRow.hidden = true; authRow.style.display = "none"; }
  }
  if (AUTH) {
    $("ci-in").onclick = function () { signIn().catch(function (e) { fail(e.message); }); };
    $("ci-out").onclick = function () {
      custToken = null; custName = null; paint();
      /* Sign-out has to tear down the CONVERSATION, not just our copy of the token. The Direct
         Line conversation was signed in separately (that is what the priming message is for),
         so leaving conv/ctok/sas alive means the agent still answers personal questions for the
         visitor who just signed out - while the panel says they are signed out. The next
         question starts a fresh conversation. */
      ready = false; conv = null; ctok = null; wm = null; sas = null; authPassed = false;
      /* Drop the stored transcript too. What is on screen stays, because wiping the panel
         mid-conversation is startling, but a later reload must not replay one person's details
         to whoever opens the page next on a shared machine. */
      hist = [];
      try {
        sessionStorage.removeItem("ci_hist");
        sessionStorage.removeItem("ci_conv");
      } catch (e) {}
      sys("You are signed out. I can still answer general questions.");
    };
  }
  $("ci-send").onclick = function () { ask(); };
  $("ci-q").addEventListener("keydown", function (e) {
    if (e.isComposing) { return; }        /* Enter is committing a candidate, not sending */
    if (e.keyCode === 229) { return; }
    if (e.key === "Enter") { e.preventDefault(); ask(); }
  });
  Array.prototype.forEach.call(document.querySelectorAll("#ci-assistant .ci-chip"), function (c) {
    c.onclick = function () { ask(c.textContent); };
  });
  /* Let anything on the page open the panel: <a href="#claims-assistant"> or class="ci-open". */
  Array.prototype.forEach.call(
    document.querySelectorAll('a[href="#claims-assistant"],.ci-open'), function (a) {
      a.addEventListener("click", function (e) { e.preventDefault(); open_(true); });
    });

  /* Signing in leaves and re-enters the page, and the conversation id already survives that.
     The words on screen did not, so the reader came back to an empty panel and a repeated
     greeting. Replay what was said, unanimated. */
  function replay() {
    var saved = null;
    try { saved = JSON.parse(sessionStorage.getItem("ci_hist") || "null"); } catch (e) {}
    if (!saved) { return false; }
    if (!saved.length) { return false; }
    hist = saved;
    for (var i = 0; i < saved.length; i++) {
      var it = saved[i];
      if (it.w === "sys") { paint_sys(it.t, false); } else { paint_msg(it.w, it.t, false); }
    }
    toBottom();
    return true;
  }

  paint();
  if (replay()) {
    greeted = true;                       /* do not greet a reader we have already greeted */
    chipsIntoLog();                       /* suggestions belong with the transcript */
    toBottom();
  }
  var code = AUTH ? new URLSearchParams(location.search).get("code") : null;
  var wasOpen = false;
  try { wasOpen = sessionStorage.getItem("ci_open") === "1"; } catch (e) {}
  if (code) {
    open_(true);
    try {
      var saved2 = JSON.parse(sessionStorage.getItem("ci_conv") || "null");
      if ((saved2 || {}).conv) {
        dl = saved2.dl; conv = saved2.conv; ctok = saved2.ctok; wm = saved2.wm;
        /* re-checked on the way back in: storage is not a trust boundary either */
        sas = tokenPostOk(saved2.sas) ? saved2.sas : null;
        ready = true;
      }
    } catch (e) {}
    finishSignIn(code).catch(function (e) { fail(e.message); });
  } else if (wasOpen) {
    open_(true);
  }
})();
