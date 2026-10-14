/* Claim form. Submits a claim, then polls until the assessment lands.
 *
 * TWO PHASE ON PURPOSE: an assessment measured 11 to 40 seconds, which is longer than a shared
 * host will hold a PHP request open. /assess starts it, /assess-poll finishes it.
 *
 * NO CREDENTIAL IS PRESENT HERE, and none is possible: the browser only ever talks to this
 * site. Everything that needs a token happens in PHP, on the server.
 */
(function () {
  var $ = function (id) { return document.getElementById(id); };
  if (!$('c365-cf')) { return; }

  var CF   = window.C365ACF || {};   /* { root }, from wp_localize_script */
  var root = CF.root || '/wp-json/claims365/v1';

  /* The bands the schema allows, spelled for a reader. If you change the enum in the agent's
     schema, change this map with it - an unknown value falls through as its raw enum name,
     which is ugly but never wrong. */
  var BAND = {
    under_500: 'under 500',
    '500_to_2000': '500 to 2,000',
    '2000_to_10000': '2,000 to 10,000',
    over_10000: 'over 10,000',
    cannot_estimate: 'cannot estimate'
  };

  /* A CDN in front of this site may cache REST responses and answer HIT without consulting the
     origin, which would serve one visitor's cached "still working" poll, or one visitor's
     result, to the next. Origin no-store headers cannot evict an entry a CDN never revalidates,
     so every call carries a unique query string and is therefore never a cache hit. The origin
     also sends no-store: see the filter in the plugin's main file. Both halves, or neither.
     Applied to the WHOLE url: with Plain permalinks the root already carries ?rest_route=..., so
     the buster has to join it with & rather than start a second query string. */
  function bust(url) {
    return url + (url.indexOf('?') < 0 ? '?' : '&') + '_=' + Date.now() + '.'
      + Math.random().toString(36).slice(2);
  }

  function say(msg) { $('c365-cf-status').textContent = msg || ''; }

  /* Every answer is read as text first. Most are JSON, but a web server or proxy in front of
     WordPress can answer with an HTML page - a 413 when the images exceed its upload limit is
     the usual one - and parsing that as JSON would show the claimant "Unexpected token <". */
  function post(path, body) {
    return fetch(bust(root + path), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    }).then(function (r) {
      return r.text().then(function (txt) {
        try { return JSON.parse(txt); } catch (e) {
          return { message: r.status === 413
            ? 'The images are too large for this site to accept. Please attach smaller files.'
            : 'The site answered unexpectedly (HTTP ' + r.status + '). Please try again later.' };
        }
      });
    });
  }

  function b64(file) {
    return new Promise(function (res, rej) {
      var r = new FileReader();
      r.onload = function () { res(r.result.split(',')[1]); };
      r.onerror = function () { rej(new Error('Could not read ' + file.name)); };
      r.readAsDataURL(file);
    });
  }

  /* ------------------------------------------------------------------ inline previews.
   * The browser already holds both files (b64 reads them for upload), so showing them costs
   * nothing: an object URL per file, no server round trip, no new endpoint. The same URLs are
   * reused in the result card, and they are byte-identical to what the server sent for
   * assessment, because they ARE the submitted bytes. */
  function esc(t) {
    var d = document.createElement('span');
    d.textContent = t === null || t === undefined ? '' : String(t);
    return d.innerHTML;
  }
  /* innerHTML escapes <, > and &, but not quotes, so a value placed inside an attribute needs
     its quotes escaped too: a file named 'a" onerror="...' must stay a file name. */
  function escAttr(t) { return esc(t).replace(/"/g, '&quot;'); }
  function fsize(n) {
    if (!(n >= 0)) { return ''; }
    return n < 1048576 ? Math.max(1, Math.round(n / 1024)) + ' KB' : (n / 1048576).toFixed(1) + ' MB';
  }
  var picked = { photo: null, quote: null };
  function figureHTML(f, label) {
    return '<img src="' + escAttr(f.url) + '" alt="' + escAttr(label + ': ' + f.name) + '">' +
      '<figcaption><b>' + esc(label) + '</b>' + esc(f.name) +
      (f.size ? ' - ' + fsize(f.size) : '') + '</figcaption>';
  }
  function wirePreview(kind, label) {
    $('c365-cf-' + kind).addEventListener('change', function () {
      var fig = $('c365-cf-' + kind + '-prev');
      var file = this.files ? this.files[0] : null;
      if (picked[kind]) { try { URL.revokeObjectURL(picked[kind].url); } catch (e) {} }
      if (!file) { picked[kind] = null; fig.hidden = true; fig.innerHTML = ''; return; }
      picked[kind] = { url: URL.createObjectURL(file), name: file.name, size: file.size, label: label };
      fig.innerHTML = figureHTML(picked[kind], label);
      fig.hidden = false;
      /* A file slipping past the picker's filter may not preview; keep the caption so the
       * claimant still sees what is attached, and let the server-side checks judge it. */
      fig.querySelector('img').onerror = function () { this.remove(); };
    });
  }
  wirePreview('photo', 'Damage photograph');
  wirePreview('quote', 'Repair quote');

  /* ------------------------------------------------------------------ the verdict card.
   * Built from innerHTML, so everything the model wrote goes through esc() first. The three
   * routing values are the product of the whole exercise, so they get the colour. */
  /* The reference the DESK issued, kept from the /assess response. The model is told the
   * reference in the prompt and normally echoes it back in claim_reference, but "normally" is
   * not "always" - and a card headed with a reference the claimant was never given, against a
   * Dataverse row filed under the one they were, is a support call nobody can unpick later.
   * The issued value wins; the model's echo is only a fallback for a resubmitted claim. */
  var issuedRef = '';
  /* What the server says it actually wrote to Dataverse: the claim and its files on /assess, the
     verdict on the poll. Never assumed from the configuration. */
  var claimLogged = false;

  function render(a, seconds, logged) {
    var cls = {
      complex_claims_desk: 'desk', straight_through: 'through', handler_review: 'review'
    }[a.routing] || 'review';
    var reasons = (a.consistency ? a.consistency.reasons : null) || [];
    var damage  = a.damage || {};
    var doc     = a.document || {};
    var cons    = a.consistency || {};

    var evidence = '';
    if (picked.photo || picked.quote) {
      evidence = '<div class="c365-cf-evidence">' +
        [picked.photo, picked.quote].map(function (f) {
          return f ? '<figure class="c365-cf-figure">' + figureHTML(f, f.label) + '</figure>' : '';
        }).join('') + '</div>';
    }

    $('c365-cf-out').innerHTML =
      '<div class="c365-cf-result">' +
        '<div class="c365-cf-ref">Claim ' + esc(issuedRef || a.claim_reference) + '</div>' +
        '<p><span class="c365-cf-flag ' + cls + '">' +
          esc(String(a.routing).replace(/_/g, ' ')) + '</span></p>' +
        evidence +
        '<dl class="c365-cf-kv">' +
          '<dt>What we saw</dt><dd>' + esc(damage.severity) + ', ' + esc(damage.description) + '</dd>' +
          '<dt>Our own estimate</dt><dd><b>' +
            esc(BAND[a.estimated_cost_band] || a.estimated_cost_band) + '</b></dd>' +
          '<dt>Amount claimed</dt><dd><b>' + esc(doc.total_claimed_gbp) + '</b></dd>' +
          '<dt>Evidence agrees?</dt><dd><b>' + esc(cons.verdict) + '</b></dd>' +
          '<dt>Confidence</dt><dd>' + esc(a.confidence) + '</dd>' +
        '</dl>' +
        (reasons.length
          ? '<p><b>Why</b></p><ul>' + reasons.map(function (x) { return '<li>' + esc(x) + '</li>'; }).join('') + '</ul>'
          : '') +
        '<p class="c365-cf-hint">Assessed in ' + seconds + 's.' +
          (logged ? ' Logged to Dataverse.' : '') + '</p>' +
      '</div>';
  }

  function done() { $('c365-cf-go').disabled = false; }

  function poll(ids, started, tries) {
    if (tries > 40) {
      say('The assessment is taking longer than expected.' +
          (claimLogged ? ' Your claim is logged.' : ''));
      done();
      return;
    }
    post('/assess-poll', ids).then(function (d) {
      if (d.status === 'working') {
        say('Assessing. ' + Math.round((Date.now() - started) / 1000) + 's so far.');
        setTimeout(function () { poll(ids, started, tries + 1); }, 3000);
        return;
      }
      if (d.status === 'complete') {
        say('');
        render(d.assessment, Math.round((Date.now() - started) / 1000), d.logged === true);
      } else if (d.status === 'failed') {
        say('The claim was submitted, but the assessment failed: ' + (d.error || 'unknown'));
      } else {
        say(d.message || 'Could not read the assessment.');
      }
      done();
    }).catch(function (e) { say('Could not read the assessment: ' + e.message); done(); });
  }

  $('c365-cf-go').addEventListener('click', function () {
    var photo = $('c365-cf-photo').files[0], quote = $('c365-cf-quote').files[0];
    if (!photo || !quote) { say('Attach both a damage photograph and a repair quote.'); return; }
    $('c365-cf-go').disabled = true;
    $('c365-cf-out').innerHTML = '';
    $('c365-cf-issued').hidden = true;
    say('Submitting your claim...');
    var started = Date.now();

    Promise.all([b64(photo), b64(quote)]).then(function (parts) {
      return post('/assess', {
        reference: $('c365-cf-ref').value,
        policy: $('c365-cf-pol').value,
        cause: $('c365-cf-cause').value,
        photo: { name: photo.name, data: parts[0] },
        quote: { name: quote.name, data: parts[1] }
      });
    }).then(function (d) {
      if (!d || !d.response) {
        say((d && d.message) ? d.message : 'The claim could not be submitted.');
        done();
        return;
      }
      /* The reference comes back on THIS response, not the poll, so a claimant who left the
       * field blank is told their reference immediately rather than forty seconds later. It
       * goes in its own element because render() replaces the whole of #c365-cf-out. */
      issuedRef = d.reference || '';
      claimLogged = d.logged === true;
      if (d.reference && d.issued) {
        var iss = $('c365-cf-issued');
        iss.innerHTML = 'Your claim reference is <b>' + esc(d.reference) +
          '</b>. Please keep it.';
        iss.hidden = false;
      }
      say(d.reference ? ('Claim ' + d.reference + ' submitted. Assessing...') : 'Claim submitted. Assessing...');
      poll({ response: d.response }, started, 0);
    }).catch(function (e) { say(e.message); done(); });
  });
})();
