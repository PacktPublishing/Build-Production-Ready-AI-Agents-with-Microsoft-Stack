<?php
/**
 * Plugin Name: Claims 365 Assistant
 * Description: A Copilot Studio chat widget and a Microsoft Foundry claim assessor, as two shortcodes. Everything is configured in Settings; nothing is hardcoded.
 * Version:     1.0.2
 * Author:      Build Production-Ready AI Agents with the Microsoft Stack
 * License:     GPL-2.0-or-later
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS IS
 *
 * The two website integrations from the book, for one site with one agent:
 *
 *   [claims365_assistant]   a floating chat widget that talks to a published Copilot
 *                           Studio agent over Direct Line, with optional customer
 *                           sign-in so the agent can answer about the caller's own data.
 *
 *   [claims365_claim_form]  a form that takes a claim plus two images, runs them past a
 *                           Microsoft Foundry agent, and shows the typed verdict. It can
 *                           also write the claim and the verdict to Dataverse.
 *
 * ---------------------------------------------------------------------------
 * WHERE THE CREDENTIALS LIVE, AND WHY IT IS WORTH READING THIS PARAGRAPH
 *
 * NO SECRET IS PRESENT IN THIS SOURCE. Every id, hostname and secret is a setting with
 * an empty default, so a fresh install of this plugin can reach nothing at all until
 * somebody fills the settings page in.
 *
 * The client secret is the only real secret here, and it is used ONLY on the server: the
 * browser never receives it, and no REST response this plugin returns contains it. You
 * have two places to keep it, and the second is better:
 *
 *   1. The settings page, which stores it in the WordPress options table.
 *   2. wp-config.php, as
 *          define( 'C365A_CLIENT_SECRET', '...' );
 *      which takes precedence over the option and keeps the secret out of the database
 *      entirely - so a database dump, a migration or a staging clone does not carry it.
 *
 * If you fill the settings page in, the value is never echoed back into the form. The
 * field renders empty with a "stored" placeholder, and saving it empty keeps what is
 * already there. That is deliberate: a secret in the HTML of an admin page is a secret in
 * every browser cache, proxy log and screenshot of that page.
 *
 * ---------------------------------------------------------------------------
 * WHAT A VISITOR CAN DO, AND WHAT THEY CANNOT
 *
 * The claim form's two REST routes are public, because a claimant is not signed in. So the
 * server decides everything that matters: which agent runs (a setting), which Dataverse row an
 * assessment writes to (remembered server-side, never taken from the browser), what an upload
 * is (read from its bytes), and what a visitor is told when something fails (a sentence, with
 * the detail kept in the error log and on the claim's row).
 *
 * @package claims365-assistant
 */

defined( 'ABSPATH' ) || exit;

define( 'C365A_VERSION', '1.0.2' );
define( 'C365A_DIR', plugin_dir_path( __FILE__ ) );
define( 'C365A_URL', plugin_dir_url( __FILE__ ) );

/** The REST namespace both shortcodes talk to. */
define( 'C365A_NS', 'claims365/v1' );

/* -------------------------------------------------------------------- settings access */

/**
 * Public settings: safe to send to a browser. Ids and hostnames, no secrets.
 *
 * These genuinely are not secrets, and it is worth being precise about why rather than
 * hoping. A Copilot Studio agent's schema name and its environment host are both needed by
 * the browser to open a Direct Line conversation, so they are in the page source of every
 * site that runs a widget like this one. A public OAuth client id is public by definition -
 * that is what "public client" means, and it is why the sign-in below uses PKCE rather than
 * a secret.
 *
 * What that does mean: publishing an agent for anonymous access makes it callable by anyone
 * who reads your page source. Metered capacity is spent per call, so treat "anonymous" as a
 * decision about cost and abuse, not only about convenience. See README.md, "Before you go
 * live".
 */
function c365a_opt( $key, $default = '' ) {
	$o = get_option( 'c365a_settings', array() );
	if ( ! is_array( $o ) ) {
		return $default;
	}
	$v = isset( $o[ $key ] ) ? trim( (string) $o[ $key ] ) : '';
	return '' === $v ? $default : $v;
}

/** Server-side identity: tenant, client id, Foundry project, Dataverse org. */
function c365a_id( $key, $default = '' ) {
	$o = get_option( 'c365a_identity', array() );
	if ( ! is_array( $o ) ) {
		return $default;
	}
	$v = isset( $o[ $key ] ) ? trim( (string) $o[ $key ] ) : '';
	return '' === $v ? $default : $v;
}

/**
 * The client secret. A wp-config.php constant wins over the stored option, so a site can
 * run this plugin with no secret in its database at all.
 */
function c365a_secret() {
	if ( defined( 'C365A_CLIENT_SECRET' ) && C365A_CLIENT_SECRET ) {
		return (string) C365A_CLIENT_SECRET;
	}
	return c365a_id( 'secret' );
}

/** Is the server side wired up enough to call Foundry? */
function c365a_can_assess() {
	return c365a_id( 'tenant' ) && c365a_id( 'client_id' ) && c365a_secret()
		&& c365a_id( 'foundry_account' ) && c365a_id( 'foundry_project' );
}

/** Is the chat widget wired up enough to open a conversation? */
function c365a_can_chat() {
	return c365a_opt( 'agent_schema' ) && c365a_opt( 'env_host' );
}

/** Optional: write claims and verdicts to Dataverse. Off unless an org URL is set. */
function c365a_dataverse_url() {
	return rtrim( c365a_id( 'dataverse_url' ), '/' );
}

/** Optional: let a customer sign in so the agent can answer about their own records. */
function c365a_customer_signin_on() {
	return c365a_opt( 'customer_authority' ) && c365a_opt( 'customer_client_id' );
}

/**
 * A small notice rendered in place of a shortcode that has not been configured. It is shown
 * to administrators only: a visitor gets nothing rather than a broken widget or a hint about
 * how the site is put together.
 */
function c365a_setup_notice( $what ) {
	if ( ! current_user_can( 'manage_options' ) ) {
		return '';
	}
	// The shortcodes return this BEFORE they enqueue anything, so the notice has to bring its
	// own stylesheet or it renders as unstyled paragraphs in the middle of the page.
	wp_enqueue_style( 'c365a-assistant', C365A_URL . 'assets/assistant.css', array(), C365A_VERSION );
	$url = admin_url( 'options-general.php?page=claims365-assistant' );
	return '<div class="c365a-setup"><p><strong>Claims 365 Assistant is not configured yet.</strong></p>'
		. '<p>' . esc_html( $what ) . ' <a href="' . esc_url( $url ) . '">Open the settings page</a>.</p>'
		. '<p class="c365a-setup-note">Only administrators can see this message.</p></div>';
}

/* ---------------------------------------------------------------------- the two halves */

require_once C365A_DIR . 'includes/settings.php';
require_once C365A_DIR . 'includes/assistant.php';
require_once C365A_DIR . 'includes/claim-form.php';

/* ------------------------------------------------------------------------ cache control */

/**
 * NEVER LET A CDN CACHE THESE RESPONSES. This is not defensive boilerplate; it is a defect
 * that happened, and it is worth knowing about before it happens to you.
 *
 * The site this was first built for sat behind a CDN that cached responses for seven days and
 * answered HIT without consulting the origin. The symptom was not "stale page": it was an agent list and
 * an in-progress assessment poll being served from a snapshot, so one visitor's result could
 * be handed to the next. It also looked exactly like a failed deploy, because the old
 * response shape survived an update, a deactivate and a reactivate.
 *
 * Two halves, and you want both:
 *   - The origin marks every response in this namespace never-store (here).
 *   - The browser adds a unique query string to every call (in the two JS files), because
 *     an origin header cannot evict an entry a CDN never revalidates.
 */
add_filter( 'rest_post_dispatch', function ( $result, $server, $request ) {
	if ( 0 === strpos( $request->get_route(), '/' . C365A_NS ) ) {
		$result->header( 'Cache-Control', 'no-store, no-cache, must-revalidate, max-age=0' );
		$result->header( 'Pragma', 'no-cache' );
		$result->header( 'X-Accel-Expires', '0' );
	}
	return $result;
}, 10, 3 );

/**
 * Optional, and off by default. If your host caches HTML and you cannot evict it, the page
 * holding the widget can go stale in ways that look like the widget is broken. Setting
 *     define( 'C365A_NO_HTML_CACHE', true );
 * in wp-config.php marks front-end HTML never-store as well.
 *
 * Leave it off on a site that legitimately wants page caching. It is here because on that
 * same site it was the difference between a correct origin and a visitor seeing an
 * eight-hour-old copy of the page.
 */
add_action( 'send_headers', function () {
	if ( ! defined( 'C365A_NO_HTML_CACHE' ) || ! C365A_NO_HTML_CACHE ) {
		return;
	}
	if ( is_admin() || wp_doing_ajax() || defined( 'REST_REQUEST' ) || is_user_logged_in() ) {
		return;
	}
	if ( is_feed() || is_robots() ) {
		return;
	}
	header( 'Cache-Control: no-store, no-cache, must-revalidate, max-age=0', true );
	header( 'Pragma: no-cache', true );
}, 99 );
