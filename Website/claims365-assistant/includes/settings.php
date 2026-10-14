<?php
/**
 * The settings page. Everything this plugin needs is here, and nothing is hardcoded
 * anywhere else.
 *
 * Two option rows, split on purpose rather than for tidiness:
 *
 *   c365a_settings   PUBLIC. Ids and hostnames the browser is given, because the browser
 *                    cannot open a Direct Line conversation without them.
 *   c365a_identity   PRIVATE. The application identity the SERVER uses to call Foundry and
 *                    Dataverse. None of it is ever sent to a browser.
 *
 * Keeping them in separate rows means the rule "public settings may be localised into a
 * script, identity may not" is something you can see rather than something you have to
 * remember.
 *
 * @package claims365-assistant
 */

defined( 'ABSPATH' ) || exit;

add_action( 'admin_menu', function () {
	add_options_page(
		'Claims 365 Assistant',
		'Claims 365 Assistant',
		'manage_options',
		'claims365-assistant',
		'c365a_settings_page'
	);
} );

add_action( 'admin_init', function () {
	register_setting( 'c365a', 'c365a_settings', array(
		'sanitize_callback' => 'c365a_sanitise_settings',
	) );
	register_setting( 'c365a', 'c365a_identity', array(
		'sanitize_callback' => 'c365a_sanitise_identity',
	) );
} );

/*
 * The identity row holds the client secret, and WordPress loads every autoloaded option on every
 * request, front end included. This one is needed only when a claim is assessed or this page is
 * open, so it is kept out of that load (WordPress 6.4 and later).
 */
foreach ( array( 'add_option_c365a_identity', 'update_option_c365a_identity' ) as $c365a_hook ) {
	add_action( $c365a_hook, function () {
		if ( function_exists( 'wp_set_option_autoload' ) ) {
			wp_set_option_autoload( 'c365a_identity', false );
		}
	} );
}

/**
 * Keep a value only if it has the shape it must have; otherwise blank it and say why on the page.
 *
 * Several of these values are joined into URLs the SERVER calls with a bearer token, so a value
 * that is not the shape of an account name or an org URL is refused rather than trusted: a Foundry
 * account name of `attacker.example/x` would otherwise send the token there.
 */
function c365a_valid( $value, $pattern, $label ) {
	if ( '' === $value || preg_match( $pattern, $value ) ) {
		return $value;
	}
	add_settings_error( 'c365a', 'c365a_' . md5( $label ), $label . ' was not saved: "' . $value
		. '" does not look like one. Check the hint under the field.' );
	return '';
}

/** Public settings: plain text, trimmed, length-capped. */
function c365a_sanitise_settings( $in ) {
	$out  = array();
	$keys = array(
		'agent_schema', 'env_host', 'api_version', 'assistant_title', 'assistant_greeting',
		'assistant_chips', 'customer_authority', 'customer_client_id', 'customer_scopes',
		'prime_message',
	);
	foreach ( $keys as $k ) {
		$v = isset( $in[ $k ] ) ? (string) $in[ $k ] : '';
		$v = ( 'assistant_chips' === $k ) ? sanitize_textarea_field( $v ) : sanitize_text_field( $v );
		$out[ $k ] = substr( trim( $v ), 0, 512 );
	}
	// A pasted Direct Line URL is a much more likely paste than a bare schema name, so read
	// one out of it rather than refusing it: this is the single most likely thing to be got wrong.
	if ( preg_match( '#botsbyschema/([A-Za-z0-9_]+)#', $out['agent_schema'], $m ) ) {
		$out['agent_schema'] = $m[1];
	}
	// And a host pasted with its scheme or a trailing slash still works.
	$out['env_host'] = preg_replace( '#^https?://#', '', $out['env_host'] );
	$out['env_host'] = rtrim( $out['env_host'], '/' );

	// The same shapes the widget's script checks before it builds a URL from them.
	$out['agent_schema'] = c365a_valid( $out['agent_schema'], '/^[A-Za-z0-9_]{1,128}$/', 'Agent schema name' );
	$out['env_host']     = c365a_valid( $out['env_host'],
		'/^[A-Za-z0-9.\-]{10,120}\.environment\.api\.powerplatform\.com$/', 'Environment host' );
	$out['customer_authority'] = rtrim( $out['customer_authority'], '/' );
	$out['customer_authority'] = c365a_valid( $out['customer_authority'],
		'#^https://[A-Za-z0-9.\-]+(/[A-Za-z0-9.\-]+)*$#', 'Sign-in authority' );
	return $out;
}

/**
 * Identity. The secret is WRITE-ONLY: an empty submission keeps whatever is stored, so the
 * form never has to render the value it is protecting.
 */
function c365a_sanitise_identity( $in ) {
	$old  = get_option( 'c365a_identity', array() );
	$old  = is_array( $old ) ? $old : array();
	$out  = array();
	$keys = array(
		'tenant', 'client_id', 'foundry_account', 'foundry_project', 'foundry_agent',
		'dataverse_url', 'dataverse_table',
	);
	foreach ( $keys as $k ) {
		$out[ $k ] = substr( trim( sanitize_text_field( isset( $in[ $k ] ) ? (string) $in[ $k ] : '' ) ), 0, 256 );
	}
	$out['dataverse_url'] = rtrim( $out['dataverse_url'], '/' );

	$guid = '/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i';
	$out['tenant']          = c365a_valid( $out['tenant'], '/^([0-9a-f-]{36}|[A-Za-z0-9.\-]+\.[A-Za-z]{2,})$/i', 'Tenant id' );
	$out['client_id']       = c365a_valid( $out['client_id'], $guid, 'Client id' );
	$out['foundry_account'] = c365a_valid( $out['foundry_account'], '/^[A-Za-z0-9][A-Za-z0-9\-]{1,63}$/', 'Foundry account name' );
	$out['foundry_project'] = c365a_valid( $out['foundry_project'], '/^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$/', 'Foundry project name' );
	$out['foundry_agent']   = c365a_valid( $out['foundry_agent'], '/^[A-Za-z0-9][A-Za-z0-9_.\-]{0,127}$/', 'Assessing agent' );
	$out['dataverse_url']   = c365a_valid( $out['dataverse_url'],
		'#^https://[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.(dynamics\.com|microsoftdynamics\.us|dynamics\.cn)$#i',
		'Organisation URL' );
	$out['dataverse_table'] = c365a_valid( $out['dataverse_table'], '/^[A-Za-z0-9_]{1,128}$/', 'Table (entity set name)' );

	$secret = isset( $in['secret'] ) ? trim( (string) $in['secret'] ) : '';
	if ( '' === $secret ) {
		$out['secret'] = isset( $old['secret'] ) ? $old['secret'] : '';
	} elseif ( '__clear__' === $secret ) {
		$out['secret'] = '';
	} else {
		$out['secret'] = $secret;
	}
	return $out;
}

function c365a_field( $group, $key, $label, $hint = '', $placeholder = '', $size = 60 ) {
	$val = ( 'c365a_settings' === $group ) ? c365a_opt( $key ) : c365a_id( $key );
	echo '<tr><th scope="row"><label for="c365a-' . esc_attr( $key ) . '">' . esc_html( $label ) . '</label></th><td>';
	echo '<input type="text" id="c365a-' . esc_attr( $key ) . '" name="' . esc_attr( $group ) . '[' . esc_attr( $key ) . ']"'
		. ' size="' . intval( $size ) . '" value="' . esc_attr( $val ) . '"'
		. ' placeholder="' . esc_attr( $placeholder ) . '" autocomplete="off" spellcheck="false">';
	if ( $hint ) {
		echo '<p class="description">' . wp_kses( $hint, array( 'code' => array(), 'em' => array(), 'strong' => array(), 'a' => array( 'href' => array() ) ) ) . '</p>';
	}
	echo '</td></tr>';
}

function c365a_settings_page() {
	if ( ! current_user_can( 'manage_options' ) ) {
		wp_die( 'Not allowed' );
	}
	$secret_stored = c365a_id( 'secret' ) ? true : false;
	$secret_pinned = defined( 'C365A_CLIENT_SECRET' ) && C365A_CLIENT_SECRET;
	?>
	<div class="wrap">
		<h1>Claims 365 Assistant</h1>
		<p>Two shortcodes. Put <code>[claims365_assistant]</code> on any page to get the chat
		   widget, and <code>[claims365_claim_form]</code> on a page to get the claim form.
		   The widget floats, so the page it sits on does not matter much; the form renders
		   where you put it.</p>

		<form method="post" action="options.php">
			<?php settings_fields( 'c365a' ); ?>

			<h2>The chat agent</h2>
			<p>Your published Copilot Studio agent. These two values are all the browser needs
			   to open a conversation, and they reach it in the page source.</p>
			<table class="form-table" role="presentation">
				<?php
				c365a_field( 'c365a_settings', 'agent_schema', 'Agent schema name',
					'In Copilot Studio: <strong>Settings &rarr; Advanced &rarr; Metadata</strong>. Looks like <code>cr1a2_myAgentName</code>. Pasting the whole Direct Line token URL works too.',
					'cr1a2_myAgentName' );
				c365a_field( 'c365a_settings', 'env_host', 'Environment host',
					'The Power Platform environment your agent lives in. Take the environment GUID, strip the hyphens, put a dot before the last two characters, and append <code>.environment.api.powerplatform.com</code>. A schema name is unique only <em>within</em> an environment, so getting this wrong returns 404 on a perfectly good agent.',
					'0000000000000000000000000000.00.environment.api.powerplatform.com', 70 );
				c365a_field( 'c365a_settings', 'api_version', 'API version',
					'Leave blank unless you have a reason. Default: <code>api-version=2022-03-01-preview</code>.',
					'api-version=2022-03-01-preview', 40 );
				?>
			</table>

			<h2>What the widget says</h2>
			<table class="form-table" role="presentation">
				<?php
				c365a_field( 'c365a_settings', 'assistant_title', 'Panel title', '', 'Claims assistant', 40 );
				c365a_field( 'c365a_settings', 'assistant_greeting', 'Opening line',
					'Shown once, when the panel is first opened.',
					'Hello. Ask me about cover, claims or our approved repairers.', 70 );
				?>
				<tr>
					<th scope="row"><label for="c365a-assistant_chips">Suggested questions</label></th>
					<td>
						<textarea id="c365a-assistant_chips" name="c365a_settings[assistant_chips]"
						          rows="4" cols="70"><?php echo esc_textarea( c365a_opt( 'assistant_chips' ) ); ?></textarea>
						<p class="description">One per line, up to four. They appear under the greeting
						   and are a good place to put the questions you know the agent answers well.</p>
					</td>
				</tr>
			</table>

			<h2>Customer sign-in <span class="c365a-optional">optional</span></h2>
			<p>Leave these blank and the widget answers anonymously, with no Sign in button at
			   all. Fill them in and a customer can sign in with their own account, so the agent
			   can answer about their own records rather than only about published material.</p>
			<p><strong>This half only works if the agent asks for it.</strong> The sign-in
			   prompt comes from an <em>Authenticate</em> node in one of the agent's topics. An
			   input merely pinned to the signed-in user's token does not prompt: it resolves
			   empty and the call behind it fails. See README.md, "Customer sign-in".</p>
			<table class="form-table" role="presentation">
				<?php
				c365a_field( 'c365a_settings', 'customer_authority', 'Sign-in authority',
					'Your customer directory. For an Entra External ID tenant: <code>https://&lt;tenant&gt;.ciamlogin.com/&lt;tenant-guid&gt;</code>. For a workforce tenant: <code>https://login.microsoftonline.com/&lt;tenant-guid&gt;</code>.',
					'', 70 );
				c365a_field( 'c365a_settings', 'customer_client_id', 'Sign-in client id',
					'A <strong>public client / SPA</strong> app registration, with this site\'s page URL listed as a redirect URI. It uses PKCE and holds no secret.',
					'', 60 );
				c365a_field( 'c365a_settings', 'customer_scopes', 'Scopes',
					'Default: <code>openid profile email offline_access</code>.', '', 60 );
				c365a_field( 'c365a_settings', 'prime_message', 'Priming message',
					'Sent silently to the agent straight after sign-in, to make it raise its authentication prompt while we hold the token. Default: <code>sign me in</code>.',
					'', 40 );
				?>
			</table>

			<h2>Assessing identity <span class="c365a-private">server-side only</span></h2>
			<p>The application this site authenticates as when it calls Microsoft Foundry and,
			   if you configure it, Dataverse. <strong>None of this reaches a browser.</strong></p>
			<table class="form-table" role="presentation">
				<?php
				c365a_field( 'c365a_identity', 'tenant', 'Tenant id', 'The directory your app registration lives in.', '', 50 );
				c365a_field( 'c365a_identity', 'client_id', 'Client id', 'A <strong>confidential</strong> app registration, separate from the sign-in one above.', '', 50 );
				?>
				<tr>
					<th scope="row"><label for="c365a-secret">Client secret</label></th>
					<td>
						<?php if ( $secret_pinned ) : ?>
							<p><strong>Set in <code>wp-config.php</code></strong> as
							   <code>C365A_CLIENT_SECRET</code>, which takes precedence over
							   anything stored here. That is the better place for it, and there
							   is nothing to do on this page.</p>
						<?php else : ?>
							<input type="password" id="c365a-secret" name="c365a_identity[secret]"
							       size="50" value="" autocomplete="new-password"
							       placeholder="<?php echo $secret_stored ? 'stored - leave blank to keep it' : 'paste the secret'; ?>">
							<p class="description">
								Write-only. The stored value is never rendered into this page, so
								saving the form with this box empty keeps what is already there.
								Type <code>__clear__</code> to remove it.
								<br><strong>Better still, do not store it here at all.</strong> Put
								<code>define( 'C365A_CLIENT_SECRET', '...' );</code> in
								<code>wp-config.php</code> and it never touches the database, so a
								dump, a migration or a staging clone does not carry it.
							</p>
						<?php endif; ?>
					</td>
				</tr>
				<?php
				c365a_field( 'c365a_identity', 'foundry_account', 'Foundry account name',
					'The resource name only. The plugin builds <code>https://&lt;name&gt;.services.ai.azure.com</code> from it.', '', 40 );
				c365a_field( 'c365a_identity', 'foundry_project', 'Foundry project name',
					'The project inside that account. It is the security and billing boundary for your agents.', '', 40 );
				c365a_field( 'c365a_identity', 'foundry_agent', 'Assessing agent',
					'The name you gave the agent in the Foundry portal. For a portal-built agent the <em>name is the id</em>.', '', 40 );
				?>
			</table>

			<h2>Dataverse <span class="c365a-optional">optional</span></h2>
			<p>Leave the org URL blank and the form still assesses claims; it simply does not
			   write them down, and the result is shown and then forgotten. Fill it in and every
			   claim is logged before assessment starts, so a claim that fails assessment still
			   leaves a record.</p>
			<p>The app registration above must be a Dataverse <strong>application user</strong>
			   with a security role that can create and update the table.
			   <code>reference/dataverse-table.md</code> lists the columns.</p>
			<table class="form-table" role="presentation">
				<?php
				c365a_field( 'c365a_identity', 'dataverse_url', 'Organisation URL',
					'For example <code>https://orgNNNNNNNN.crm4.dynamics.com</code>. Blank turns Dataverse logging off.', '', 60 );
				c365a_field( 'c365a_identity', 'dataverse_table', 'Table (entity set name)',
					'The plural set name, not the display name. Default: <code>cp_submissions</code>.', '', 40 );
				?>
			</table>

			<?php submit_button( 'Save settings' ); ?>
		</form>

		<hr>
		<h2>Status</h2>
		<?php c365a_status_block(); ?>

		<style>
			.c365a-optional, .c365a-private {
				font-size: 11px; text-transform: uppercase; letter-spacing: .08em;
				vertical-align: middle; padding: 2px 7px; border-radius: 9px; font-weight: 600;
			}
			.c365a-optional { background: #f0f0f1; color: #50575e; }
			.c365a-private  { background: #f6e8d5; color: #7a4f0a; }
			.c365a-status li { margin: 0 0 .4em; }
		</style>
	</div>
	<?php
}

/**
 * Say what works, in the order a person debugging it would ask. Each line is a fact this
 * page checked just now, not a restatement of what was typed into the form.
 */
function c365a_status_block() {
	echo '<ul class="c365a-status">';

	if ( c365a_can_chat() ) {
		$url  = 'https://' . c365a_opt( 'env_host' ) . '/powervirtualagents/botsbyschema/'
			. rawurlencode( c365a_opt( 'agent_schema' ) ) . '/directline/token?'
			. c365a_opt( 'api_version', 'api-version=2022-03-01-preview' );
		$res  = wp_remote_get( $url, array( 'timeout' => 12 ) );
		$code = is_wp_error( $res ) ? 0 : wp_remote_retrieve_response_code( $res );
		if ( is_wp_error( $res ) ) {
			echo '<li><strong>Chat agent:</strong> could not reach Copilot Studio: '
				. esc_html( $res->get_error_message() ) . '</li>';
		} elseif ( 200 === $code ) {
			/*
			 * A token proves the agent exists and is published; it does not prove the agent will
			 * answer an anonymous caller. An agent whose authentication was never saved as No
			 * authentication still issues a token, and then refuses the conversation.
			 */
			echo '<li><strong>Chat agent:</strong> found and published: Copilot Studio issued a '
				. 'conversation token for it. Ask it a question through the widget to confirm it '
				. 'answers without sign-in.</li>';
		} elseif ( 401 === $code || 403 === $code ) {
			echo '<li><strong>Chat agent:</strong> found, but <strong>not published for anonymous '
				. 'access</strong>. In Copilot Studio set Security &rarr; Authentication to '
				. '<em>No authentication</em>, save, confirm the second dialog, then publish. '
				. 'Note that the environment also has to allow it &mdash; see README.md.</li>';
		} elseif ( 404 === $code ) {
			echo '<li><strong>Chat agent:</strong> no agent with that schema name in that '
				. 'environment. A schema name is unique only within one environment, so check '
				. 'the host as well as the name.</li>';
		} else {
			echo '<li><strong>Chat agent:</strong> Copilot Studio returned ' . intval( $code ) . '.</li>';
		}
	} else {
		echo '<li><strong>Chat agent:</strong> not configured.</li>';
	}

	if ( ! c365a_can_assess() ) {
		echo '<li><strong>Foundry:</strong> not configured.</li>';
	} else {
		$agents = c365a_cf_agents();
		if ( is_wp_error( $agents ) && 403 === intval( $agents->get_error_data()['status'] ?? 0 ) ) {
			/*
			 * Foundry Agent Consumer, the role the README recommends, can run the agent named in
			 * the settings but cannot list or read the project's agents (measured 13 October
			 * 2026). That is a 403 here and a working claim form, so say so rather than alarm.
			 */
			echo '<li><strong>Foundry:</strong> the token was accepted, but the project refused to list '
				. 'its agents (HTTP 403). With <em>Foundry Agent Consumer</em> that is expected: the role '
				. 'runs the agent named below and cannot read the agent list. Submit a claim to test it, '
				. 'or add <em>Foundry User</em> if you want the list here.</li>';
		} elseif ( is_wp_error( $agents ) ) {
			echo '<li><strong>Foundry:</strong> configured, but the project said: '
				. esc_html( $agents->get_error_message() ) . '</li>';
		} else {
			// Escape each name FIRST, then join with the markup. Escaping after the join would
			// escape the markup too, and the page would read "</code>, <code>" out loud.
			$names = array();
			foreach ( array_slice( $agents, 0, 12 ) as $a ) {
				$names[] = '<code>' . esc_html( $a['name'] ) . '</code>';
			}
			echo '<li><strong>Foundry:</strong> working. ' . count( $agents )
				. ' agent(s) visible: ' . implode( ', ', $names )
				. ( count( $agents ) > 12 ? ', and more' : '' ) . '</li>';
			$want = c365a_id( 'foundry_agent' );
			if ( $want ) {
				echo '<li><strong>Assessing agent:</strong> '
					. ( c365a_cf_agent_exists( $want ) ? 'found.' : '<strong>not found</strong> in that project.' )
					. '</li>';
			}
		}
	}

	if ( c365a_dataverse_url() ) {
		$probe = c365a_cf_dv( 'GET', c365a_id( 'dataverse_table', 'cp_submissions' ) . '?$top=1&$select=createdon' );
		echo '<li><strong>Dataverse:</strong> '
			. ( is_wp_error( $probe )
				? 'configured, but the org said: ' . esc_html( $probe->get_error_message() )
				: 'reachable, and the table answers.' )
			. '</li>';
	} else {
		echo '<li><strong>Dataverse:</strong> off. Claims are assessed but not logged.</li>';
	}

	echo '<li><strong>Client secret:</strong> '
		. ( defined( 'C365A_CLIENT_SECRET' ) && C365A_CLIENT_SECRET
			? 'read from <code>wp-config.php</code>, not from the database.'
			: ( c365a_id( 'secret' ) ? 'stored in the database. Consider moving it to <code>wp-config.php</code>.' : 'not set.' ) )
		. '</li>';

	echo '</ul>';
}
