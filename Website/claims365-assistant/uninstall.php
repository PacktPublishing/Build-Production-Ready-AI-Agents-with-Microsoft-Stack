<?php
/**
 * Runs when the plugin is DELETED from the Plugins screen, not when it is deactivated.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS FILE EXISTS, AND WHY IT MATTERS MORE THAN MOST UNINSTALL HOOKS.
 *
 * WordPress does not touch a plugin's options when the plugin is removed. Without this file,
 * deleting Claims 365 Assistant would leave `c365a_identity` sitting in wp_options - and that
 * row holds the CLIENT SECRET. Every later database dump, migration, staging clone and backup
 * would carry a live credential belonging to a plugin the site no longer has, with nothing on
 * the Plugins screen to suggest it was there.
 *
 * "Deleting it left the secret behind" is not a defect anyone goes looking for, so the fix is
 * to make deletion mean deletion.
 *
 * Both options are removed. If you want to keep your configuration across a reinstall, take a
 * copy of the settings first, or move the secret into wp-config.php as C365A_CLIENT_SECRET,
 * which this file cannot and should not touch.
 *
 * @package claims365-assistant
 */

// The uninstall constant, not ABSPATH: this file must run only through WordPress's own
// uninstall path, never by being requested directly.
defined( 'WP_UNINSTALL_PLUGIN' ) || exit;

delete_option( 'c365a_identity' );
delete_option( 'c365a_settings' );

// The cached client-credentials tokens, which are transients rather than options. They expire
// on their own, but leaving a bearer token in the database after the plugin is gone is the
// same mistake as leaving the secret.
global $wpdb;
// phpcs:disable WordPress.DB.DirectDatabaseQuery -- there is no options API for a LIKE sweep
$wpdb->query(
	"DELETE FROM {$wpdb->options}
	  WHERE option_name LIKE '\_transient\_c365a\_%'
	     OR option_name LIKE '\_transient\_timeout\_c365a\_%'"
);
// phpcs:enable

// Multisite: the options above are per site, so clear each one rather than only the site the
// deletion happened to be triggered from.
if ( is_multisite() ) {
	$sites = get_sites( array( 'fields' => 'ids', 'number' => 0 ) );
	foreach ( $sites as $site_id ) {
		switch_to_blog( $site_id );
		delete_option( 'c365a_identity' );
		delete_option( 'c365a_settings' );
		restore_current_blog();
	}
}
