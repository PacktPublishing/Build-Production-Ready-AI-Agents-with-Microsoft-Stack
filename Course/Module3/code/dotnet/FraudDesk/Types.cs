// Message types for the fraud desk.
//
// DEMONSTRATION CODE - written to be read rather than deployed.
//
// One type per edge, exactly as in the Python track. Delivery is by type: if two executors accept
// the same type, both receive the message, which is a class of bug that produces no output and no
// error. Intake takes SeedClaim and emits VerifiedClaim rather than re-sending what it received,
// which is what keeps every edge distinct.
//
// These records are saved INTO checkpoints. Change one while an investigation is waiting and that
// checkpoint may no longer restore; finish or discard paused runs before changing them.
namespace FraudDesk;

public sealed record SeedClaim(string ClaimRef);

public sealed record VerifiedClaim(
    string ClaimRef, string ClaimantRef, string Repairer, double AmountClaimed);

// LinkType and SharedWith are the investigator's label for how a person was found. Evidence is what
// the RECORDS say links them to the others, as the tools returned it, one entry per identifier
// ("bank_account with CLT-9005"): a person can hold several, and the challenge step judges all.
public sealed record LinkedClaimant(
    string ClaimantRef, string FullName, string WhyLinked, string LinkType,
    List<string> SharedWith, List<string> Evidence);

public sealed record TraversalComplete(
    string SeedClaimRef, List<LinkedClaimant> Candidates, int Hops, string Narrative);

public sealed record JudgedClaimant(string ClaimantRef, string Reason, string Strength);

public sealed record ReferralProposal(
    string SeedClaimRef, List<JudgedClaimant> Confirmed, List<JudgedClaimant> Excluded,
    bool RecommendRefer, string Rationale, int Hops, List<string> ClaimRefs, double ExposureGbp);

// The human gate. Accusing a customer of fraud is as consequential as paying them, so it is a
// person's decision, and the desk may wait days for it.
public sealed record ReferralApprovalRequest(
    string SeedClaimRef, int ClaimantCount, int ClaimCount, double ExposureGbp,
    string Rationale, List<string> ConfirmedRefs, List<string> ExcludedRefs);

public sealed record ReferralDecision(bool Approved, string Approver, string Note);

public sealed record FraudOutcome(
    string SeedClaimRef, string Decision, int ClaimantCount, int ClaimCount,
    double ExposureGbp, int Hops, string Approver, string ReferralRef,
    List<string> AuditTrail);
