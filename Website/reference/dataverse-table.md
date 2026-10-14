# The Dataverse table, if you want one

**Optional.** Leave the organisation URL blank on the settings page and the claim form still
works: it assesses the claim and shows the verdict, and nothing is written down. Everything below
only matters if you want a record.

The value of having one is worth stating, because it is the reason the plugin writes twice rather
than once. **The claim row is created BEFORE the assessment starts.** A claim that fails
assessment - a model timeout, a malformed answer, a quota refusal - still leaves a record with the
evidence attached and a status of `failed`, so nothing a claimant sent you is lost to a failure on
your side. The verdict is a second write onto the same row.

## The table

**If you imported the tenant setup pack (the `Tenant-Setup` folder), this table already exists** as `cp_submission`,
entity set `cp_submissions`, with every column below — there is nothing to build. One caveat: the
pack's *Claims 365 Attendee* role grants **no** privilege on this table, so the application user
the plugin authenticates as still needs a role that can Create, Read and Write it.


One custom table. The publisher prefix below is `cp_`; yours will differ, and the plugin does not
care - see *Different column names* at the foot.

| Column | Type | Written | Holds |
|---|---|---|---|
| `cp_name` | Text (100) | on submit | the claim reference. The table's **primary name** column |
| `cp_policynumber` | Text (50) | on submit | as typed on the form |
| `cp_reportedcause` | Text, multiple lines | on submit | what the claimant said happened |
| `cp_submittedby` | Text (200) | on submit | the literal `website` |
| `cp_triagestatus` | Text (20) | both | `submitted`, then `running`, then `complete` or `failed` |
| `cp_damagephoto` | **File** | on submit | the damage photograph, as uploaded |
| `cp_repairquote` | **File** | on submit | the supporting document, as uploaded |
| `cp_routing` | Text (40) | on verdict | `straight_through`, `handler_review`, `complex_claims_desk` |
| `cp_consistencyverdict` | Text (20) | on verdict | does the document agree with the photograph |
| `cp_estimatedcostband` | Text (20) | on verdict | the model's own band, not the document's number |
| `cp_damageseverity` | Text (20) | on verdict | `cosmetic`, `minor`, `moderate`, `severe` |
| `cp_totalclaimedgbp` | Decimal | on verdict | what the document asks for |
| `cp_fraudsuspected` | Yes/No | on verdict | |
| `cp_confidence` | Decimal (2dp) | on verdict | the model's confidence in its own assessment |
| `cp_rationale` | Text, multiple lines | on verdict | one or two sentences for a human reader |
| `cp_consistencyreasons` | Text, multiple lines | on verdict | the reasons, newline-separated |
| `cp_triagejson` | Text, multiple lines (max length) | on verdict | the whole assessment, verbatim |
| `cp_triagedon` | Date and time | on verdict | UTC |
| `cp_triageerror` | Text, multiple lines | on failure | why it failed, truncated to 3,900 characters |

Three notes on that table that will save you a rebuild:

**`cp_damagephoto` and `cp_repairquote` are File columns, not images and not notes.** The plugin
PATCHes raw bytes to `.../cp_submissions(id)/cp_damagephoto` with an
`x-ms-file-name` header, which is the File column upload path. An Image column has a different
one, and an attachment on the Notes table is a different table entirely.

**Keep `cp_triagejson` as long as the type allows.** It is the only column that survives you
changing your mind about which fields you care about, and it is what you will want the first time
you disagree with a routing decision.

**Store the band, not just the number.** `cp_estimatedcostband` and `cp_totalclaimedgbp` are two
different claims about money made by two different parties, and the whole point of the assessment
is the case where they disagree. Collapsing them into one column throws the finding away.

## Permissions

The app registration on the settings page must be a Dataverse **application user**:

1. Power Platform admin centre, your environment, **Settings**, **Users + permissions**,
   **Application users**, **New app user**.
2. Pick the app registration, pick a business unit, and give it a security role that can
   **Create**, **Read** and **Write** on your table.
3. Nothing else. It does not need System Administrator, and giving it that is how a demo
   integration ends up able to delete production data.

The Dataverse token audience is the **organisation URL** - `https://yourorg.crm4.dynamics.com/.default`,
not a fixed Microsoft resource. The plugin builds it from the URL you enter, so getting the URL
right gets the audience right.

## Different column names

Nothing above is baked in. The plugin holds one map, and a filter can rewrite any part of it -
put this in your theme's `functions.php` or a small site plugin:

```php
add_filter( 'c365a_dv_columns', function ( $c ) {
    $c['routing']   = 'new_routingdecision';
    $c['reference'] = 'new_claimref';
    return $c;
} );
```

The keys are the plugin's own names for the fields (`id`, `reference`, `policy`, `cause`,
`submitted_by`, `status`, `photo`, `quote`, `routing`, `verdict`, `cost_band`, `severity`,
`total_claimed`, `fraud`, `confidence`, `rationale`, `reasons`, `json`, `assessed_on`, `error`).
The table's own entity set name is a setting rather than a filter, because it changes far more
often than the columns do.
