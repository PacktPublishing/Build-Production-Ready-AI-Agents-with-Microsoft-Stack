<?php
/**
 * [claims365_assistant] - the chat widget.
 *
 * A floating launcher and a chat panel that talks to a published Copilot Studio agent over
 * Direct Line. No SDK, no bundler, no external script: one CSS file and one JS file, both
 * served from this plugin.
 *
 * This file only renders the markup and hands the browser its configuration. Everything
 * interesting is in assets/assistant.js, which carries the measured traps in its comments.
 *
 * NOTHING SECRET IS LOCALISED HERE. The schema name, the environment host and the public
 * sign-in client id are all values a browser must have in order to work at all, and they are
 * in the page source of every site running a widget like this one.
 *
 * @package claims365-assistant
 */

defined( 'ABSPATH' ) || exit;

/** Up to four suggestion chips, one per line in the settings. */
function c365a_chips() {
	$raw = c365a_opt( 'assistant_chips' );
	if ( '' === $raw ) {
		return array();
	}
	$out = array();
	foreach ( preg_split( '/\r\n|\r|\n/', $raw ) as $line ) {
		$line = trim( $line );
		if ( '' !== $line ) {
			$out[] = $line;
		}
		if ( count( $out ) >= 4 ) {
			break;
		}
	}
	return $out;
}

function c365a_assistant_shortcode() {
	if ( ! c365a_can_chat() ) {
		return c365a_setup_notice( 'The chat widget needs an agent schema name and an environment host.' );
	}

	wp_enqueue_style( 'c365a-assistant', C365A_URL . 'assets/assistant.css', array(), C365A_VERSION );
	wp_enqueue_script( 'c365a-assistant', C365A_URL . 'assets/assistant.js', array(), C365A_VERSION, true );
	wp_localize_script( 'c365a-assistant', 'C365A', array(
		'envHost'    => c365a_opt( 'env_host' ),
		'schema'     => c365a_opt( 'agent_schema' ),
		'apiVersion' => c365a_opt( 'api_version', 'api-version=2022-03-01-preview' ),
		'greeting'   => c365a_opt( 'assistant_greeting', 'Hello. Ask me anything about our cover and how a claim works.' ),
		'signin'     => c365a_customer_signin_on() ? array(
			'authority' => c365a_opt( 'customer_authority' ),
			'clientId'  => c365a_opt( 'customer_client_id' ),
			'scopes'    => c365a_opt( 'customer_scopes', 'openid profile email offline_access' ),
			'prime'     => c365a_opt( 'prime_message', 'sign me in' ),
		) : null,
	) );

	$title = c365a_opt( 'assistant_title', 'Claims assistant' );
	$chips = c365a_chips();
	$auth  = c365a_customer_signin_on();

	ob_start();
	?>
	<div id="ci-assistant">

		<button type="button" id="ci-launch"
		        aria-expanded="false" aria-haspopup="dialog" aria-controls="ci-panel">
			<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
			     stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
				<path d="M21 11.5a8.4 8.4 0 0 1-9 8.4 9.9 9.9 0 0 1-2.8-.4L3 21l1.6-4.7A8.2 8.2 0 0 1 3.6 11.5a8.4 8.4 0 0 1 9-8.4 8.4 8.4 0 0 1 8.4 8.4z"/>
			</svg>
			<span>Ask us</span>
			<?php
			/*
			 * The launcher carries NO aria-label. An accessible name that does not contain the
			 * visible words fails WCAG 2.5.3 Label in Name, and "click Ask us" then does nothing
			 * in voice control. The visible text names it; this span appends the rest, and the
			 * unread state, out of sight.
			 */
			?>
			<span class="ci-sr" id="ci-launchsr"> - <?php echo esc_html( strtolower( $title ) ); ?></span>
			<span class="ci-dot" aria-hidden="true"></span>
		</button>

		<div id="ci-panel" hidden role="dialog" aria-labelledby="ci-title">
			<div id="ci-head">
				<div id="ci-avatar" aria-hidden="true">
					<?php
					/*
					 * The face. Pure decoration, and the one thing in the widget that reads as
					 * somebody being there rather than a text box with a border. It blinks,
					 * nods, glances, waves and thinks; it stops dead under
					 * prefers-reduced-motion, and it never runs behind a closed panel or a
					 * hidden tab. Delete this <svg> and everything else still works.
					 */
					?>
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
					<h2 id="ci-title"><?php echo esc_html( $title ); ?></h2>
					<div id="ci-status">
						<span class="ci-pip" aria-hidden="true"></span>
						<span id="ci-statust"><?php echo $auth ? 'Not signed in' : 'Ready'; ?></span>
					</div>
				</div>
				<button type="button" id="ci-min" aria-label="Minimise <?php echo esc_attr( strtolower( $title ) ); ?>" title="Minimise">
					<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"
					     stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
						<path d="M6 9l6 6 6-6"/>
					</svg>
				</button>
				<?php /* Cloned into every bot row. A mark, not the face: the arch says "the assistant said this", the face says "the assistant is here". */ ?>
				<span id="ci-arch" hidden aria-hidden="true"><svg viewBox="0 0 24 24" fill="none"
				      stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true">
					<path d="M5 21V11a7 7 0 0 1 14 0v10"/><circle cx="12" cy="13.4" r="2.1" fill="currentColor" stroke="none"/>
				</svg></span>
			</div>

			<div id="ci-log" role="log" aria-live="polite" aria-label="Conversation" tabindex="0"></div>

			<div id="ci-chips" role="group" aria-label="Suggested questions">
				<?php foreach ( $chips as $chip ) : ?>
					<button type="button" class="ci-chip"><?php echo esc_html( $chip ); ?></button>
				<?php endforeach; ?>
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
				<?php if ( $auth ) : ?>
					<div id="ci-auth">
						<span id="ci-authnote">Sign in to ask about your own records</span>
						<span>
							<button type="button" id="ci-in">Sign in</button>
							<button type="button" id="ci-out" class="ci-quiet" hidden>Sign out</button>
						</span>
					</div>
				<?php endif; ?>
			</div>
		</div>

		<?php
		/*
		 * Announcements live OUTSIDE the transcript: the typing dots are decorative, and an
		 * assertive failure must not fight the polite running commentary of the log.
		 */
		?>
		<p id="ci-live" class="ci-sr" role="status" aria-live="polite"></p>
		<p id="ci-alert" class="ci-sr" role="alert"></p>
	</div>
	<?php
	return ob_get_clean();
}
add_shortcode( 'claims365_assistant', 'c365a_assistant_shortcode' );
