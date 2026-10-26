using System;
using System.Collections.Generic;
using System.Globalization;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class AgreementObligation
    {
        public string DebtorId;
        public string CreditorId;
        public string ActionId;
        public string ResourceId;
        public int Quantity = 1;
        public double DueTime;
    }

    /// <summary>Persistent evidence of consent, settlement or closure. Never removed by a goal change.</summary>
    [Serializable]
    public sealed class AgreementSettlement
    {
        public string Id;
        public string CommitmentId;
        public string ObligationId;
        public string Kind;
        public string ActorId;
        public string EvidenceId;
        public int Quantity;
        public double Time;
        public string Reason;
    }

    /// <summary>Lifecycle over Society's commitments and obligations; it owns no parallel agreement state.</summary>
    public sealed class AgreementLifecycleService
    {
        private readonly SimulationWorld world;
        private readonly List<AgreementSettlement> settlements = new List<AgreementSettlement>();

        public IReadOnlyList<AgreementSettlement> Settlements
        {
            get
            {
                var copies = new List<AgreementSettlement>(settlements.Count);
                foreach (var item in settlements) copies.Add(SnapshotClone.Copy(item));
                return copies.AsReadOnly();
            }
        }

        internal AgreementLifecycleService(SimulationWorld simulation)
        { world = simulation ?? throw new ArgumentNullException(nameof(simulation)); }

        /// <summary>Publish an offer. Every party must accept it before any obligation becomes due.</summary>
        public Commitment Propose(string kind, IList<string> parties, IList<AgreementObligation> obligations,
            double dueTime, string scopeId = null)
        {
            if (string.IsNullOrWhiteSpace(kind) || parties == null || parties.Count < 2 ||
                obligations == null || obligations.Count < 1 || !SimulationWorld.Finite(dueTime) || dueTime < world.Time)
                throw new ArgumentException("An agreement needs parties, obligations and a finite future deadline.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var party in parties)
            {
                if (string.IsNullOrWhiteSpace(party) || !unique.Add(party) || !PartyExists(party))
                    throw new ArgumentException("Agreement parties must be distinct active agents or collectives.");
            }
            foreach (var proposed in obligations)
            {
                if (proposed == null || !unique.Contains(proposed.DebtorId ?? string.Empty) ||
                    !unique.Contains(proposed.CreditorId ?? string.Empty) || proposed.DebtorId == proposed.CreditorId ||
                    proposed.Quantity <= 0 || !SimulationWorld.Finite(proposed.DueTime) ||
                    proposed.DueTime < world.Time || proposed.DueTime > dueTime ||
                    string.IsNullOrWhiteSpace(proposed.ActionId) == string.IsNullOrWhiteSpace(proposed.ResourceId))
                    throw new ArgumentException("Each obligation needs two parties, exactly one action/resource, positive quantity and a valid deadline.");
            }

            // Validate the entire offer before touching authoritative containers.
            var agreement = world.Society.CreateCommitment(kind, parties, dueTime, scopeId);
            agreement.ManagedLifecycle = true;
            agreement.CreatedAt = world.Time;
            agreement.Status = "Proposed";
            foreach (var proposed in obligations)
            {
                var obligation = new Obligation
                {
                    Id = world.NextId(), CommitmentId = agreement.Id, DebtorId = proposed.DebtorId,
                    CreditorId = proposed.CreditorId, ActionId = proposed.ActionId, ResourceId = proposed.ResourceId,
                    Quantity = proposed.Quantity, DueTime = proposed.DueTime, Status = "Proposed"
                };
                world.Society.Obligations.Add(obligation.Id, obligation);
                agreement.ObligationIds.Add(obligation.Id);
            }
            Record(agreement, null, "Proposed", null, null, 0, null);
            world.Emit("agreement.proposed", agreement.Id, null, agreement.Kind);
            return agreement;
        }

        public bool Accept(string commitmentId, string partyId, string actorId, out string reason)
        {
            if (!TryAgreement(commitmentId, out var agreement, out reason)) return false;
            if (!agreement.PartyIds.Contains(partyId ?? string.Empty) ||
                !CanRepresent(partyId, actorId, "agreement.accept", agreement.Id))
                return Refuse("PartyAuthorityMissing", out reason);
            if (HasPartyDecision(agreement.Id, "Accepted", partyId)) return true;
            if (agreement.Status != "Proposed") return Refuse("AgreementNotProposed", out reason);
            if (world.Time > EarliestDeadline(agreement)) return Refuse("OfferExpired", out reason);
            Record(agreement, null, "Accepted", actorId, partyId, 0, null);
            world.Emit("agreement.accepted", actorId, partyId, agreement.Id);
            foreach (var party in agreement.PartyIds)
                if (!HasPartyDecision(agreement.Id, "Accepted", party)) return true;
            agreement.Status = "Active";
            foreach (var id in agreement.ObligationIds) world.Society.Obligations[id].Status = "Outstanding";
            Record(agreement, null, "Activated", null, null, 0, null);
            world.Emit("agreement.activated", agreement.Id, null, agreement.Kind);
            return true;
        }

        public bool Decline(string commitmentId, string partyId, string actorId, string explanation)
        {
            if (!TryAgreement(commitmentId, out var agreement, out _) || agreement.Status != "Proposed" ||
                !agreement.PartyIds.Contains(partyId ?? string.Empty) ||
                !CanRepresent(partyId, actorId, "agreement.accept", agreement.Id)) return false;
            Close(agreement, "Declined", string.IsNullOrWhiteSpace(explanation) ? "PartyDeclined" : explanation, actorId);
            return true;
        }

        /// <summary>Every party consents to ending outstanding obligations; fulfilled quantities remain in history.</summary>
        public bool RequestTermination(string commitmentId, string partyId, string actorId,
            string explanation, out string reason)
        {
            if (!TryAgreement(commitmentId, out var agreement, out reason)) return false;
            if (!agreement.PartyIds.Contains(partyId ?? string.Empty) ||
                !CanRepresent(partyId, actorId, "agreement.terminate", agreement.Id))
                return Refuse("PartyAuthorityMissing", out reason);
            if (agreement.Status == "Terminated" && HasPartyDecision(agreement.Id, "TerminationAccepted", partyId))
                return true;
            if (agreement.Status != "Active" && agreement.Status != "Breached")
                return Refuse("AgreementNotActive", out reason);
            MarkOverdue(agreement);
            if (!HasPartyDecision(agreement.Id, "TerminationAccepted", partyId))
                Record(agreement, null, "TerminationAccepted", actorId, partyId, 0,
                    string.IsNullOrWhiteSpace(explanation) ? "MutualTermination" : explanation);
            foreach (var party in agreement.PartyIds)
                if (!HasPartyDecision(agreement.Id, "TerminationAccepted", party)) return true;
            Close(agreement, "Terminated", string.IsNullOrWhiteSpace(explanation) ? "MutualTermination" : explanation, actorId);
            return true;
        }

        /// <summary>Apply existing economic facts. A ledger quantity can settle at most one quantity of debt.</summary>
        public bool ReconcileTransfer(string obligationId, string transactionId, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(transactionId)) return Refuse("MissingTransaction", out reason);
            if (!TryObligation(obligationId, out var obligation, out var agreement, out reason)) return false;
            foreach (var record in settlements)
                if (record.ObligationId == obligation.Id && record.Kind == "Transfer" && record.Reason == transactionId)
                    return true;
            if ((agreement.Status != "Active" && agreement.Status != "Breached") ||
                (obligation.Status != "Outstanding" && obligation.Status != "Breached"))
                return Refuse("ObligationNotOutstanding", out reason);
            if (string.IsNullOrEmpty(obligation.ResourceId)) return Refuse("NotResourceObligation", out reason);
            var entries = new List<LedgerEntry>();
            foreach (var entry in world.Economy.Ledger)
                if (entry.TransactionId == transactionId && entry.FromOwnerId == obligation.DebtorId &&
                    entry.ToOwnerId == obligation.CreditorId && entry.ResourceId == obligation.ResourceId &&
                    HappenedAfterConsent(entry, agreement.Id))
                    entries.Add(entry);
            entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            int applied = 0;
            foreach (var entry in entries)
            {
                long used = 0;
                foreach (var record in settlements)
                    if (record.Kind == "Transfer" && record.EvidenceId == entry.Id) used += record.Quantity;
                int quantity = (int)Math.Min(obligation.Quantity - obligation.FulfilledQuantity,
                    Math.Max(0L, (long)entry.Quantity - used));
                if (quantity <= 0) continue;
                if (applied == 0) MarkOverdue(agreement);
                obligation.FulfilledQuantity += quantity;
                applied += quantity;
                Record(agreement, obligation, "Transfer", obligation.DebtorId, entry.Id, quantity, transactionId);
            }
            if (applied == 0) return Refuse("NoUnclaimedMatchingTransfer", out reason);
            FinishSettlement(agreement, obligation);
            return true;
        }

        /// <summary>The creditor's explicit acknowledgement is the receipt; an actor cannot self-certify its debt.</summary>
        public bool ConfirmPerformance(string obligationId, string creditorActorId, string evidenceId,
            int quantity, out string reason)
        {
            if (!TryObligation(obligationId, out var obligation, out var agreement, out reason)) return false;
            if (!CanRepresent(obligation.CreditorId, creditorActorId, "agreement.confirm", agreement.Id))
                return Refuse("CreditorAuthorityMissing", out reason);
            if (string.IsNullOrWhiteSpace(evidenceId) || quantity <= 0) return Refuse("InvalidReceipt", out reason);
            foreach (var record in settlements)
                if (record.Kind == "Performance" && record.EvidenceId == evidenceId)
                    return (record.ObligationId == obligationId && record.ActorId == creditorActorId &&
                            record.Quantity == quantity) || Refuse("ReceiptAlreadyUsed", out reason);
            if ((agreement.Status != "Active" && agreement.Status != "Breached") ||
                (obligation.Status != "Outstanding" && obligation.Status != "Breached"))
                return Refuse("ObligationNotOutstanding", out reason);
            if (string.IsNullOrEmpty(obligation.ActionId)) return Refuse("NotActionObligation", out reason);
            if (quantity > obligation.Quantity - obligation.FulfilledQuantity)
                return Refuse("QuantityExceedsOutstanding", out reason);
            MarkOverdue(agreement);
            obligation.FulfilledQuantity += quantity;
            Record(agreement, obligation, "Performance", creditorActorId, evidenceId, quantity, obligation.ActionId);
            FinishSettlement(agreement, obligation);
            return true;
        }

        public void Tick()
        {
            var ids = new List<string>();
            foreach (var agreement in world.Society.Commitments.Values)
                if (agreement.ManagedLifecycle && (agreement.Status == "Active" || agreement.Status == "Proposed" ||
                    agreement.Status == "Breached"))
                    ids.Add(agreement.Id);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var agreement = world.Society.Commitments[id];
                if (agreement.Status == "Proposed")
                {
                    if (world.Time > EarliestDeadline(agreement)) Close(agreement, "Expired", "OfferDeadlinePassed", null);
                    continue;
                }
                MarkOverdue(agreement);
            }
        }

        internal void Restore(IEnumerable<AgreementSettlement> saved)
        {
            var candidates = new List<AgreementSettlement>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var evidence = new Dictionary<string, long>(StringComparer.Ordinal);
            var performance = new HashSet<string>(StringComparer.Ordinal);
            var fulfilled = new Dictionary<string, long>(StringComparer.Ordinal);
            if (saved != null)
                foreach (var item in saved)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || SequenceOf(item.Id) < 1 || !ids.Add(item.Id) ||
                        !world.Society.Commitments.TryGetValue(item.CommitmentId ?? string.Empty, out var agreement) ||
                        !agreement.ManagedLifecycle || !SimulationWorld.Finite(item.Time) || item.Time < agreement.CreatedAt ||
                        item.Time > world.Time || !ValidRecordKind(item.Kind))
                        throw new InvalidOperationException("Invalid agreement history.");
                    if (item.Kind == "Transfer" || item.Kind == "Performance")
                    {
                        if (!world.Society.Obligations.TryGetValue(item.ObligationId ?? string.Empty, out var obligation) ||
                            obligation.CommitmentId != item.CommitmentId || item.Quantity <= 0 ||
                            string.IsNullOrWhiteSpace(item.EvidenceId))
                            throw new InvalidOperationException("Invalid agreement settlement.");
                        fulfilled.TryGetValue(obligation.Id, out var prior);
                        fulfilled[obligation.Id] = prior + item.Quantity;
                        if (item.Kind == "Transfer")
                        {
                            var entry = world.Economy.Ledger.Find(row => row.Id == item.EvidenceId);
                            if (entry == null || entry.TransactionId != item.Reason || entry.FromOwnerId != obligation.DebtorId ||
                                entry.ToOwnerId != obligation.CreditorId || entry.ResourceId != obligation.ResourceId ||
                                entry.Time < agreement.CreatedAt || entry.Time > item.Time)
                                throw new InvalidOperationException("Agreement settlement lacks matching economic evidence.");
                            evidence.TryGetValue(entry.Id, out var used);
                            evidence[entry.Id] = used + item.Quantity;
                            if (evidence[entry.Id] > entry.Quantity)
                                throw new InvalidOperationException("Agreement history reuses economic quantity.");
                        }
                        else if (string.IsNullOrEmpty(obligation.ActionId) || !performance.Add(item.EvidenceId))
                            throw new InvalidOperationException("Agreement history reuses performance evidence.");
                    }
                    else if (item.Quantity != 0) throw new InvalidOperationException("Non-settlement history carries a quantity.");
                    candidates.Add(SnapshotClone.Copy(item));
                }
            foreach (var agreement in world.Society.Commitments.Values)
            {
                if (!agreement.ManagedLifecycle) continue;
                if (!SimulationWorld.Finite(agreement.CreatedAt) || !SimulationWorld.Finite(agreement.DueTime) ||
                    agreement.CreatedAt < 0 || agreement.CreatedAt > world.Time || agreement.DueTime < agreement.CreatedAt ||
                    agreement.PartyIds == null || agreement.PartyIds.Count < 2 ||
                    new HashSet<string>(agreement.PartyIds, StringComparer.Ordinal).Count != agreement.PartyIds.Count ||
                    agreement.ObligationIds == null || agreement.ObligationIds.Count < 1 ||
                    new HashSet<string>(agreement.ObligationIds, StringComparer.Ordinal).Count != agreement.ObligationIds.Count)
                    throw new InvalidOperationException("Invalid managed agreement.");
                foreach (var id in agreement.ObligationIds)
                {
                    if (!world.Society.Obligations.TryGetValue(id ?? string.Empty, out var obligation) ||
                        obligation.CommitmentId != agreement.Id || obligation.Quantity <= 0 ||
                        obligation.FulfilledQuantity < 0 || obligation.FulfilledQuantity > obligation.Quantity ||
                        !SimulationWorld.Finite(obligation.DueTime) || obligation.DueTime < agreement.CreatedAt ||
                        obligation.DueTime > agreement.DueTime || !agreement.PartyIds.Contains(obligation.DebtorId) ||
                        !agreement.PartyIds.Contains(obligation.CreditorId) || obligation.DebtorId == obligation.CreditorId ||
                        string.IsNullOrWhiteSpace(obligation.ActionId) == string.IsNullOrWhiteSpace(obligation.ResourceId))
                        throw new InvalidOperationException("Invalid managed obligation.");
                    fulfilled.TryGetValue(id, out var quantity);
                    if (quantity != obligation.FulfilledQuantity)
                        throw new InvalidOperationException("Agreement quantity disagrees with settlement history.");
                }
                ValidateHistory(agreement, candidates);
            }
            settlements.Clear();
            settlements.AddRange(candidates);
        }

        private void ValidateHistory(Commitment agreement, List<AgreementSettlement> history)
        {
            string status = null;
            double activationTime = 0, lastTime = agreement.CreatedAt, closedAt = 0;
            long activationSequence = 0;
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            var terminating = new HashSet<string>(StringComparer.Ordinal);
            var breached = new HashSet<string>(StringComparer.Ordinal);
            var quantities = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var id in agreement.ObligationIds) quantities[id] = 0;
            foreach (var item in history)
            {
                if (item.CommitmentId != agreement.Id) continue;
                if (item.Time < lastTime) throw new InvalidOperationException("Agreement history is not chronological.");
                lastTime = item.Time;
                bool open = status == "Active" || status == "Breached";
                switch (item.Kind)
                {
                    case "Proposed":
                        if (status != null || item.Time != agreement.CreatedAt) throw InvalidTransition();
                        status = "Proposed";
                        break;
                    case "Accepted":
                        if (status != "Proposed" || !agreement.PartyIds.Contains(item.EvidenceId ?? string.Empty) ||
                            string.IsNullOrWhiteSpace(item.ActorId) || !accepted.Add(item.EvidenceId)) throw InvalidTransition();
                        break;
                    case "Activated":
                        if (status != "Proposed" || accepted.Count != agreement.PartyIds.Count ||
                            item.Time > EarliestDeadline(agreement)) throw InvalidTransition();
                        status = "Active";
                        activationTime = item.Time;
                        activationSequence = SequenceOf(item.Id);
                        break;
                    case "Transfer":
                    case "Performance":
                        if (!open || !quantities.ContainsKey(item.ObligationId ?? string.Empty)) throw InvalidTransition();
                        quantities[item.ObligationId] += item.Quantity;
                        if (quantities[item.ObligationId] > world.Society.Obligations[item.ObligationId].Quantity)
                            throw InvalidTransition();
                        if (item.Kind == "Transfer")
                        {
                            var entry = world.Economy.Ledger.Find(row => row.Id == item.EvidenceId);
                            if (entry.Time < activationTime || SequenceOf(entry.Id) <= activationSequence)
                                throw new InvalidOperationException("Transfer predates agreement consent.");
                        }
                        break;
                    case "ObligationBreached":
                        if (!open || !quantities.ContainsKey(item.ObligationId ?? string.Empty) ||
                            item.Time <= world.Society.Obligations[item.ObligationId].DueTime ||
                            quantities[item.ObligationId] >= world.Society.Obligations[item.ObligationId].Quantity ||
                            !breached.Add(item.ObligationId)) throw InvalidTransition();
                        break;
                    case "Breached":
                        if (status != "Active" || breached.Count < 1) throw InvalidTransition();
                        status = "Breached";
                        break;
                    case "TerminationAccepted":
                        if (!open || !agreement.PartyIds.Contains(item.EvidenceId ?? string.Empty) ||
                            string.IsNullOrWhiteSpace(item.ActorId) || !terminating.Add(item.EvidenceId)) throw InvalidTransition();
                        break;
                    case "Terminated":
                        if (!open || terminating.Count != agreement.PartyIds.Count) throw InvalidTransition();
                        status = "Terminated";
                        closedAt = item.Time;
                        break;
                    case "Fulfilled":
                        if (!open) throw InvalidTransition();
                        foreach (var id in agreement.ObligationIds)
                            if (quantities[id] != world.Society.Obligations[id].Quantity) throw InvalidTransition();
                        status = "Fulfilled";
                        closedAt = item.Time;
                        break;
                    case "Expired":
                    case "Declined":
                        if (status != "Proposed" || (item.Kind == "Expired" && item.Time <= EarliestDeadline(agreement)))
                            throw InvalidTransition();
                        status = item.Kind;
                        closedAt = item.Time;
                        break;
                }
            }
            if (status != agreement.Status || agreement.ClosedAt != closedAt)
                throw new InvalidOperationException("Agreement state disagrees with its lifecycle history.");
            foreach (var id in agreement.ObligationIds)
            {
                var obligation = world.Society.Obligations[id];
                var expected = quantities[id] == obligation.Quantity ? "Fulfilled" :
                    (status == "Terminated" || status == "Declined" || status == "Expired") ? "Cancelled" :
                    status == "Proposed" ? "Proposed" : breached.Contains(id) ? "Breached" : "Outstanding";
                if (obligation.Status != expected)
                    throw new InvalidOperationException("Obligation state disagrees with its lifecycle history.");
            }
        }

        private static InvalidOperationException InvalidTransition()
        { return new InvalidOperationException("Invalid agreement lifecycle transition in snapshot."); }

        private bool TryAgreement(string id, out Commitment agreement, out string reason)
        {
            reason = null;
            if (!world.Society.Commitments.TryGetValue(id ?? string.Empty, out agreement) || !agreement.ManagedLifecycle)
                return Refuse("UnknownManagedAgreement", out reason);
            return true;
        }

        private bool TryObligation(string id, out Obligation obligation, out Commitment agreement, out string reason)
        {
            agreement = null;
            reason = null;
            if (!world.Society.Obligations.TryGetValue(id ?? string.Empty, out obligation))
                return Refuse("UnknownObligation", out reason);
            return TryAgreement(obligation.CommitmentId, out agreement, out reason);
        }

        private bool PartyExists(string id)
        {
            return world.Agents.ContainsKey(id) ||
                (world.Society.Collectives.TryGetValue(id, out var collective) && collective.Active);
        }

        private bool CanRepresent(string partyId, string actorId, string actionId, string commitmentId)
        {
            if (!world.Agents.ContainsKey(actorId ?? string.Empty)) return false;
            if (partyId == actorId) return true;
            return world.Society.Collectives.TryGetValue(partyId ?? string.Empty, out var collective) &&
                collective.Active && world.Society.HasAuthority(actorId, actionId, partyId, objectId: commitmentId);
        }

        private bool HasPartyDecision(string id, string kind, string partyId)
        {
            foreach (var item in settlements)
                if (item.CommitmentId == id && item.Kind == kind && item.EvidenceId == partyId) return true;
            return false;
        }

        private bool HappenedAfterConsent(LedgerEntry entry, string id)
        {
            foreach (var item in settlements)
                if (item.CommitmentId == id && item.Kind == "Activated")
                    return entry.Time >= item.Time && SequenceOf(entry.Id) > SequenceOf(item.Id);
            return false;
        }

        private long SequenceOf(string id)
        {
            var prefix = world.WorldId + "/";
            return id != null && id.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(id.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                && sequence > 0 && sequence <= world.CurrentSequence ? sequence : -1;
        }

        private double EarliestDeadline(Commitment agreement)
        {
            var result = agreement.DueTime;
            foreach (var id in agreement.ObligationIds) result = Math.Min(result, world.Society.Obligations[id].DueTime);
            return result;
        }

        private void FinishSettlement(Commitment agreement, Obligation obligation)
        {
            if (obligation.FulfilledQuantity == obligation.Quantity) obligation.Status = "Fulfilled";
            world.Emit("agreement.obligation_settled", obligation.DebtorId, obligation.CreditorId, obligation.Id);
            foreach (var id in agreement.ObligationIds)
                if (world.Society.Obligations[id].Status != "Fulfilled") return;
            Close(agreement, "Fulfilled", null, null);
        }

        private void MarkOverdue(Commitment agreement)
        {
            foreach (var id in agreement.ObligationIds)
            {
                var obligation = world.Society.Obligations[id];
                if (obligation.Status != "Outstanding" || world.Time <= obligation.DueTime) continue;
                obligation.Status = "Breached";
                Record(agreement, obligation, "ObligationBreached", obligation.DebtorId, null, 0, "DeadlinePassed");
                world.Emit("agreement.obligation_breached", obligation.DebtorId, obligation.CreditorId, obligation.Id);
                if (agreement.Status == "Active")
                {
                    agreement.Status = "Breached";
                    agreement.CloseReason = "ObligationDeadlinePassed:" + obligation.Id;
                    Record(agreement, null, "Breached", obligation.DebtorId, null, 0, agreement.CloseReason);
                    world.Emit("agreement.breached", obligation.DebtorId, null, agreement.Id);
                }
            }
        }

        private void Close(Commitment agreement, string status, string reason, string actorId)
        {
            agreement.Status = status;
            agreement.ClosedAt = world.Time;
            agreement.CloseReason = reason;
            foreach (var id in agreement.ObligationIds)
            {
                var obligation = world.Society.Obligations[id];
                if (obligation.Status == "Fulfilled") continue;
                obligation.Status = status == "Breached" ? "Breached" : "Cancelled";
            }
            Record(agreement, null, status, actorId, null, 0, reason);
            world.Emit("agreement." + status.ToLowerInvariant(), actorId ?? agreement.Id, null, agreement.Id);
        }

        private void Record(Commitment agreement, Obligation obligation, string kind,
            string actorId, string evidenceId, int quantity, string reason)
        {
            settlements.Add(new AgreementSettlement { Id = world.NextId(), CommitmentId = agreement.Id,
                ObligationId = obligation?.Id, Kind = kind, ActorId = actorId, EvidenceId = evidenceId,
                Quantity = quantity, Time = world.Time, Reason = reason });
        }

        private static bool ValidRecordKind(string kind)
        {
            return kind == "Proposed" || kind == "Accepted" || kind == "Activated" || kind == "Transfer" ||
                kind == "Performance" || kind == "TerminationAccepted" || kind == "Fulfilled" ||
                kind == "Terminated" || kind == "Breached" || kind == "ObligationBreached" ||
                kind == "Expired" || kind == "Declined";
        }

        private static bool Refuse(string value, out string reason) { reason = value; return false; }
    }
}
