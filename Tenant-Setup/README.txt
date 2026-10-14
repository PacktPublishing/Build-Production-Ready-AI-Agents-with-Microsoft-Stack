CLAIMS 365 - SETUP PACK
=======================
Build the Claims 365 prerequisites in your own Microsoft tenant, through the browser.

Open Setup-Pack-Guide.html for the click-by-click instructions. This file is the plain-text
version of what is in the pack and why it is safe to import.


WHAT THIS IS
------------
The Claims 365 exercises run on a prepared Dataverse environment: ten tables, 432 rows of
fictional insurance data, six Power Automate flows and a security role. None of that is
something you would want to rebuild by hand.

This pack is that environment, exported from the one the live course ran on. You import one
solution file, load six spreadsheets, and you have the same starting point.


NO COMPILED CODE, NO SCRIPTS - AND SIX FLOWS THAT DO RUN
--------------------------------------------------------
Being precise, because the difference matters to whoever approves this: there is no compiled code,
no script and no macro anywhere in this pack. There ARE six Power Automate flows, which are
executable logic. They run as you, they are described in full below, and you can read every one of
them before you turn it on.

The pack contains four kinds of file and no others:

  .zip   a Dataverse solution. A solution is XML in an archive - open it and read it. Inside you
         will find solution.xml, customizations.xml, [Content_Types].xml and a Workflows folder
         holding one readable JSON file per flow. Nothing else.
  .csv   plain text spreadsheets. A .csv is structurally incapable of carrying a macro. This is
         why the data ships as CSV rather than XLSX, even though both import equally well.
  .json  one schema definition, for Chapter 4.
  .txt / .html   documents.

There is no .exe, .dll, .ps1, .bat, .js, .msapp, .xlsm or any other macro-enabled or executable
file anywhere in this pack. Nothing asks you for a password, an API key or a token.

CHECKSUMS.txt lists a SHA-256 for every file. To confirm one on Windows:

    Get-FileHash "Claims365SetupPack_1_0_0_0.zip"

Inspect anything you like before importing. Do not modify and re-zip the solution, though -
editing customizations.xml invalidates it and the import will refuse the file.

The solution zip contains exactly NINE files: solution.xml, customizations.xml,
[Content_Types].xml, and six flow definitions under Workflows/. Nothing else.


IF SOMEONE HAS TO SIGN THIS OFF
-------------------------------
Four things a reviewer will find in this pack, and why each is harmless:

1. The words PluginAssembly, PluginType and SdkMessageProcessingStep DO appear in
   customizations.xml. They are not assemblies - they are the names of READ PRIVILEGES on the
   security role (prvReadPluginAssembly and so on), which Dataverse puts on any new custom role.
   A solution carrying a real plug-in ships the compiled DLL as a base64 <Content> element.
   Search for "<Content>" or "base64": both return zero. There is no WebResource either, which
   is how JavaScript would arrive, and <CustomControls /> is empty.

2. Every flow references schema.management.azure.com. That is the $schema declaration every
   Power Automate flow definition carries. It is a namespace identifier, not a call.

3. ONE GUID is hardcoded, in Claims365CustomerClaimLookup: a directory (tenant) id belonging to
   the course's demo Entra External ID tenant. It is the value the flow checks a customer's sign-in
   token was issued by. A tenant id is not a secret - every tenant's is public - and it grants
   nothing. But you MUST replace it with your own external tenant's id if you reproduce the
   customer sign-in path, or the gate will validate against a directory you do not control and
   refuse everyone. The guide says where. If you are not doing Chapter 3's customer sign-in, delete the flow.

4. ONE flow makes one genuine outbound call. "Claims 365 Customer Claim Lookup" calls
   https://graph.microsoft.com/oidc/userinfo to validate a signed-in customer's token. It is the
   identity check that makes Chapter 3's customer sign-in gate real. The other five flows talk only to
   Dataverse. If you are not reproducing the customer sign-in path, delete that one flow after
   import and nothing else changes.

What the security role grants: organisation-wide READ on the six data tables; read, create and
own-record write on the two referral tables Chapter 5's Fraud Desk writes to; and the read-only plug-in metadata
and SharePoint document-location privileges Dataverse puts on every new role. There is NO DELETE
privilege at any depth on anything, and nothing outside the ten cp_ tables.

Three consequences worth stating plainly, because they are real:
  * The flows run under a Dataverse connection holding YOUR OAuth token, so they act as you and
    can reach what you can reach in that environment. That is how any Power Automate flow works.
  * Whoever performs the import OWNS every component in it. Import it yourself.
  * On the import wizard, the checkbox labelled "Enable Plugin steps and flows included in the
    solution" sounds alarming and is not. This solution contains no plug-in steps; the setting only
    lets the six flows arrive switched on.


CONTENTS
--------
Claims365SetupPack_1_0_0_0.zip   The solution. Creates 10 cp_ tables, 6 cloud flows, 1 Dataverse
                                 connection reference and the "Claims 365 Attendee" security role.
                                 Unmanaged, so you can open the flows and read how they work.

data/Policy.csv                  3 rows   Chapters 2-3
data/Claim.csv                   3 rows   Chapters 2-3
data/Approved Repairer.csv       3 rows   Chapters 2-3
data/Claims Guidance.csv         3 rows   Chapters 2-3
data/Claimant.csv              186 rows   Chapter 5
data/Book Claim.csv            234 rows   Chapter 5
All-six-tables.zip               The same six files in one archive, for the import wizard route.

module1/M1-Part7-evaluate-testset.csv    The six-case test set for Chapter 3's evaluation.
module1/frontline-instructions.txt       Paste into the Frontline agent's instructions (Chapter 2).
module2/triage-agent-instructions.txt    Paste into the Triage agent in the Foundry portal (Chapter 4).
module2/claim_assessment.schema.json     Paste into the same agent as its output schema.

MANIFEST.json                    Machine-readable inventory: every table, flow, file and row count.
CHECKSUMS.txt                    SHA-256 for every file in the pack.
Setup-Pack-Guide.html            The instructions. Start here.

The damage photographs and repair quotes for Chapter 4 are NOT in this folder. They are in the
same repository, under Course/Module2/evidence/.


THE SHORT VERSION OF THE INSTRUCTIONS
-------------------------------------
Full detail, including what each error means, is in Setup-Pack-Guide.html.

  1. Create a Dataverse environment for this. Not your default one - it is the only teardown that
     actually removes everything afterwards, and some tenants block unmanaged imports elsewhere.
  2. In make.powerapps.com, check the environment picker, then Data > Connections > + New
     connection > Microsoft Dataverse > Create. Do this BEFORE the import.
  3. Solutions > Import solution > This device > the .zip > Next > pick your connection when the
     Connections page appears > Import.
  4. PUBLISH ALL CUSTOMIZATIONS. An unmanaged import lands in draft. Skip this and the data import
     in step 5 will not see the tables properly.
  5. Tables > Policy > Import > Import data from Excel > data/Policy.csv. Repeat for the other
     five. Order does not matter: none of these tables uses a lookup column.
  6. Solution > Connection references: confirm it is bound. Solution > Cloud flows: turn on any
     that read Off, then publish each one.
  7. Assign yourself the "Claims 365 Attendee" security role, then sign out and back in.


THREE THINGS THAT CATCH PEOPLE OUT
----------------------------------
* Whoever runs the import owns everything in it, flows included. Do the whole setup as one
  identity - your own. If an admin imports it "to help", you will not be able to turn the flows on.

* Do not re-run a data import to fix a partial one. These tables carry no alternate key, so the
  importer has nothing to match on and adds every row again. A second run leaves you with 6
  policies and 372 claimants. Delete the rows first, then import once.

* If you are on the Power Apps Developer Plan, "Import data from Excel" is not included and simply
  will not be there. Use the Data import wizard in the Power Platform admin centre instead, with
  All-six-tables.zip. Same for GCC, GCC High and DoD.


ABOUT THE POSTCODES, BEFORE ANYONE ASKS
---------------------------------------
The 186 claimant postcodes use REAL UK outward codes - M14, M20, SE15, EH8, LS7, BL3, OL9, SK4,
M32, WA15 - because the fraud scenario depends on a cluster of claimants living plausibly near one
another, and invented outward codes would look obviously synthetic. The inward halves are generated
and most are not deliverable addresses.

This is not personal data. A postcode identifies an area of roughly fifteen households, not a
person, and every name, email, phone number and bank detail attached to these postcodes is invented.
No real individual is described anywhere in this pack.

If your reviewer wants the outward codes randomised too, they carry no teaching weight - only the
CLUSTERING matters: five claimants (CLT-0061, CLT-0062, CLT-0063, CLT-9005, CLT-9006) share M20 4WX, and six rows in Book Claim.csv give it as the incident postcode. Find and replace freely, as long as those rows still match each other.


A NOTE ON THE DATA
------------------
Claims 365 and Contoso Insurance are training fiction. Every policyholder, claim, claimant, bank
account, phone number and email address is invented. The phone numbers use Ofcom's reserved drama
range, the sort codes use an unissued range, and the email addresses use the reserved .example
top-level domain, which cannot receive mail. Nothing in this pack is anyone's real data.

The pack carries no credential, no user account, no organisation URL and no tenant name. It does
carry one directory (tenant) id, inside a single flow, described in item 3 above - disclosed here
rather than left for you to find.

One string matters more than it looks. The claim CLM-2026-0387 records its settlement in words -
"Settlement of 2,700 GBP (3,200 less your 500 excess)" - while the numeric column beside it reads
3,200. Chapter 2's central lesson depends on that mismatch existing exactly as written. If you edit
the seed data, leave that row alone.
