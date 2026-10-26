using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class ResourceSlot
    {
        public string Id;
        public string PlaceId;
        public string Kind;
        public int Capacity = 1;
        public string LongTermAssigneeId;
        public bool Active = true;
        public bool RegionAvailable = true;
    }

    [Serializable]
    public sealed class ResourceToken
    {
        public string Id;
        public string SlotId;
        public string OwnerScopeId;
        public string AgentId;
        public double ExpiresAt;
        public bool Occupied;
        public bool Active = true;
        public long CancellationGeneration;
    }

    [Serializable]
    public sealed class ResourceRequest
    {
        public string Id;
        public string SlotId;
        public string OwnerScopeId;
        public string AgentId;
        public double RequestedAt;
        public double ExpiresAt;
        public double LeaseSeconds = 30;
        public string Status = "Waiting";
        public string TokenId;
    }

    public sealed class ResourceService
    {
        private readonly SimulationWorld world;
        public readonly Dictionary<string, ResourceSlot> Slots = new Dictionary<string, ResourceSlot>(StringComparer.Ordinal);
        public readonly Dictionary<string, ResourceToken> Tokens = new Dictionary<string, ResourceToken>(StringComparer.Ordinal);
        public readonly Dictionary<string, ResourceRequest> Requests = new Dictionary<string, ResourceRequest>(StringComparer.Ordinal);

        internal ResourceService(SimulationWorld world) { this.world = world; }

        public bool CanAcquire(string slotId, string agentId)
        {
            if (string.IsNullOrWhiteSpace(slotId) || string.IsNullOrWhiteSpace(agentId) ||
                !world.Agents.ContainsKey(agentId) || !Slots.TryGetValue(slotId, out var slot) || !slot.Active || !slot.RegionAvailable || slot.Capacity < 1) return false;
            if (!string.IsNullOrEmpty(slot.LongTermAssigneeId) && slot.LongTermAssigneeId != agentId) return false;
            int used = 0;
            foreach (var token in Tokens.Values)
                if (token.Active && token.SlotId == slotId && token.ExpiresAt > world.Time) used++;
            return used < slot.Capacity;
        }

        public ResourceToken TryAcquire(string slotId, string ownerScopeId, string agentId, double leaseSeconds)
        {
            if (!ValidRequest(ownerScopeId, agentId, leaseSeconds) || string.IsNullOrWhiteSpace(slotId) ||
                !Slots.TryGetValue(slotId, out var slot) || !slot.Active || slot.Capacity < 1 ||
                (!string.IsNullOrEmpty(slot.LongTermAssigneeId) && slot.LongTermAssigneeId != agentId)) return null;
            foreach (var token in Tokens.Values)
                if (token.Active && token.ExpiresAt > world.Time && token.SlotId == slotId && token.OwnerScopeId == ownerScopeId && token.AgentId == agentId)
                { token.ExpiresAt = Math.Max(token.ExpiresAt, world.Time + leaseSeconds); return token; }
            if (!CanAcquire(slotId, agentId)) return null;
            var result = new ResourceToken { Id = world.NextId(), SlotId = slotId, OwnerScopeId = ownerScopeId,
                AgentId = agentId, ExpiresAt = world.Time + leaseSeconds };
            Tokens.Add(result.Id, result);
            world.Emit("resource.reserved", ownerScopeId, agentId, slotId);
            return result;
        }

        /// <summary>All requested slots are reserved together or none are reserved.</summary>
        public List<ResourceToken> TryAcquireMany(IList<string> slotIds, string ownerScopeId, string agentId, double leaseSeconds)
        {
            if (slotIds == null || slotIds.Count == 0 || !ValidRequest(ownerScopeId, agentId, leaseSeconds)) return null;
            var sorted = new List<string>(slotIds);
            sorted.Sort(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0 && sorted[i] == sorted[i - 1]) return null;
                if (string.IsNullOrWhiteSpace(sorted[i]) || !Slots.TryGetValue(sorted[i], out var slot) ||
                    !slot.Active || slot.Capacity < 1 ||
                    (!string.IsNullOrEmpty(slot.LongTermAssigneeId) && slot.LongTermAssigneeId != agentId)) return null;
                bool alreadyHeld = false;
                foreach (var token in Tokens.Values)
                    if (token.Active && token.ExpiresAt > world.Time && token.SlotId == sorted[i] &&
                        token.OwnerScopeId == ownerScopeId && token.AgentId == agentId) { alreadyHeld = true; break; }
                if (!alreadyHeld && !CanAcquire(sorted[i], agentId)) return null;
            }
            var result = new List<ResourceToken>();
            foreach (var slot in sorted) result.Add(TryAcquire(slot, ownerScopeId, agentId, leaseSeconds));
            return result;
        }

        public ResourceRequest Request(string slotId, string ownerScopeId, string agentId, double waitSeconds, double leaseSeconds)
        {
            if (string.IsNullOrWhiteSpace(slotId) || !Slots.TryGetValue(slotId, out var slot) || !slot.Active ||
                !ValidRequest(ownerScopeId, agentId, leaseSeconds) || !PositiveFinite(waitSeconds) ||
                double.IsInfinity(world.Time + waitSeconds))
                throw new ArgumentException("Invalid resource request.");
            foreach (var pending in Requests.Values)
                if (pending.Status == "Waiting" && pending.SlotId == slotId && pending.OwnerScopeId == ownerScopeId && pending.AgentId == agentId)
                    return pending;
            var request = new ResourceRequest { Id = world.NextId(), SlotId = slotId, OwnerScopeId = ownerScopeId,
                AgentId = agentId, RequestedAt = world.Time, ExpiresAt = world.Time + waitSeconds, LeaseSeconds = leaseSeconds };
            Requests.Add(request.Id, request);
            ProcessWaiters();
            return request;
        }

        public bool Occupy(string tokenId, string agentId)
        {
            if (!Tokens.TryGetValue(tokenId, out var token) || !token.Active || token.AgentId != agentId || token.ExpiresAt <= world.Time)
                return false;
            token.Occupied = true;
            world.Emit("resource.occupied", token.OwnerScopeId, agentId, token.SlotId);
            return true;
        }

        public bool Renew(string tokenId, string ownerScopeId, double leaseSeconds)
        {
            if (string.IsNullOrEmpty(tokenId) || !Tokens.TryGetValue(tokenId, out var token) || !token.Active ||
                token.ExpiresAt <= world.Time || token.OwnerScopeId != ownerScopeId ||
                !ValidRequest(ownerScopeId, token.AgentId, leaseSeconds))
                return false;
            token.ExpiresAt = world.Time + leaseSeconds;
            return true;
        }

        public bool Transfer(string tokenId, string fromScopeId, string toScopeId)
        {
            if (string.IsNullOrEmpty(tokenId) || !Tokens.TryGetValue(tokenId, out var token) || !token.Active ||
                token.ExpiresAt <= world.Time || token.OwnerScopeId != fromScopeId ||
                string.IsNullOrWhiteSpace(toScopeId)) return false;
            token.OwnerScopeId = toScopeId;
            token.CancellationGeneration++;
            world.Emit("resource.transferred", toScopeId, token.AgentId, token.SlotId);
            return true;
        }

        public bool Release(string tokenId, string ownerScopeId)
        {
            if (!Tokens.TryGetValue(tokenId, out var token) || !token.Active || token.OwnerScopeId != ownerScopeId) return false;
            token.Active = false;
            token.Occupied = false;
            token.CancellationGeneration++;
            Tokens.Remove(tokenId);
            world.Emit("resource.released", ownerScopeId, token.AgentId, token.SlotId);
            ProcessWaiters();
            return true;
        }

        public void ReleaseScope(string ownerScopeId)
        {
            // Cancel first: releasing a token can immediately grant another pending request.
            foreach (var request in Requests.Values)
                if (request.Status == "Waiting" && request.OwnerScopeId == ownerScopeId) request.Status = "Cancelled";
            var tokenIds = new List<string>();
            foreach (var token in Tokens.Values)
                if (token.Active && token.OwnerScopeId == ownerScopeId) tokenIds.Add(token.Id);
            tokenIds.Sort(StringComparer.Ordinal);
            foreach (var tokenId in tokenIds) Release(tokenId, ownerScopeId);
        }

        public void ReleaseAgent(string agentId)
        {
            foreach (var request in Requests.Values)
                if (request.Status == "Waiting" && request.AgentId == agentId) request.Status = "Cancelled";
            var tokenIds = new List<string>();
            foreach (var token in Tokens.Values)
                if (token.Active && token.AgentId == agentId) tokenIds.Add(token.Id);
            tokenIds.Sort(StringComparer.Ordinal);
            foreach (var id in tokenIds) Release(id, Tokens[id].OwnerScopeId);
        }

        public void RemoveSlot(string slotId)
        {
            if (!Slots.TryGetValue(slotId, out var slot)) return;
            slot.Active = false;
            var tokenIds = new List<string>();
            foreach (var token in Tokens.Values) if (token.Active && token.SlotId == slotId) tokenIds.Add(token.Id);
            foreach (var tokenId in tokenIds) Release(tokenId, Tokens[tokenId].OwnerScopeId);
            foreach (var request in Requests.Values) if (request.SlotId == slotId && request.Status == "Waiting") request.Status = "Cancelled";
        }

        public void SetRegionAvailability(string slotId, bool available)
        {
            if (!Slots.TryGetValue(slotId, out var slot)) return;
            slot.RegionAvailable = available;
            if (available) return;
            foreach (var request in Requests.Values)
                if (request.SlotId == slotId && request.Status == "Waiting") request.Status = "Cancelled";
            var tokens = new List<string>();
            foreach (var token in Tokens.Values) if (token.Active && token.SlotId == slotId) tokens.Add(token.Id);
            foreach (var id in tokens) Release(id, Tokens[id].OwnerScopeId);
        }

        public void Expire()
        {
            var tokenIds = new List<string>();
            foreach (var token in Tokens.Values) if (token.Active && token.ExpiresAt <= world.Time) tokenIds.Add(token.Id);
            tokenIds.Sort(StringComparer.Ordinal);
            foreach (var id in tokenIds) Release(id, Tokens[id].OwnerScopeId);
            foreach (var request in Requests.Values)
                if (request.Status == "Waiting" && request.ExpiresAt <= world.Time) request.Status = "TimedOut";
            ProcessWaiters();
            if (Requests.Count > 2048)
            {
                var finished = new List<ResourceRequest>();
                foreach (var request in Requests.Values) if (request.Status != "Waiting") finished.Add(request);
                finished.Sort((a, b) => { int order = a.RequestedAt.CompareTo(b.RequestedAt);
                    return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id); });
                for (int i = 0; i < finished.Count && Requests.Count > 1024; i++) Requests.Remove(finished[i].Id);
            }
        }

        private static bool PositiveFinite(double value)
        { return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value); }

        private bool ValidRequest(string scope, string agent, double lease)
        { return !string.IsNullOrWhiteSpace(scope) && !string.IsNullOrWhiteSpace(agent) &&
            world.Agents.ContainsKey(agent) && PositiveFinite(lease) && !double.IsInfinity(world.Time + lease); }

        private void ProcessWaiters()
        {
            var waiting = new List<ResourceRequest>();
            foreach (var request in Requests.Values) if (request.Status == "Waiting") waiting.Add(request);
            waiting.Sort((a, b) => { var order = a.RequestedAt.CompareTo(b.RequestedAt);
                return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id); });
            foreach (var request in waiting)
            {
                if (request.ExpiresAt <= world.Time) { request.Status = "TimedOut"; continue; }
                var token = TryAcquire(request.SlotId, request.OwnerScopeId, request.AgentId, request.LeaseSeconds);
                if (token != null) { request.TokenId = token.Id; request.Status = "Granted"; }
            }
        }
    }

    [Serializable]
    public sealed class LedgerEntry
    {
        public string Id;
        public string TransactionId;
        public string FromOwnerId;
        public string ToOwnerId;
        public string ResourceId;
        public int Quantity;
        public double Time;
    }

    [Serializable]
    public sealed class TransactionReceipt
    {
        public string TransactionId;
        public string Kind;
        public string FromOwnerId;
        public string ToOwnerId;
        public string ResourceId;
        public int Quantity;
        public string CounterResourceId;
        public int CounterQuantity;

        public bool Matches(TransactionReceipt other)
        {
            return other != null && Kind == other.Kind && FromOwnerId == other.FromOwnerId &&
                ToOwnerId == other.ToOwnerId && ResourceId == other.ResourceId &&
                Quantity == other.Quantity &&
                (Kind == "Transfer" ? string.IsNullOrEmpty(CounterResourceId) &&
                    string.IsNullOrEmpty(other.CounterResourceId) : CounterResourceId == other.CounterResourceId) &&
                CounterQuantity == other.CounterQuantity;
        }
    }

    public sealed class EconomyService
    {
        private readonly SimulationWorld world;
        private readonly Dictionary<string, int> balances = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, TransactionReceipt> committedTransactions =
            new Dictionary<string, TransactionReceipt>(StringComparer.Ordinal);
        public readonly List<LedgerEntry> Ledger = new List<LedgerEntry>();

        internal EconomyService(SimulationWorld world) { this.world = world; }
        private static string Key(string ownerId, string resourceId) { return Identity.Definition(ownerId, resourceId); }
        public int Balance(string ownerId, string resourceId)
        { return balances.TryGetValue(Key(ownerId, resourceId), out var balance) ? balance : 0; }

        public void Seed(string ownerId, string resourceId, int quantity)
        {
            if (quantity < 0 || string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("Invalid inventory seed.");
            balances[Key(ownerId, resourceId)] = checked(Balance(ownerId, resourceId) + quantity);
        }

        public bool Transfer(string transactionId, string fromOwnerId, string toOwnerId, string resourceId, int quantity)
        {
            if (string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(fromOwnerId) ||
                string.IsNullOrWhiteSpace(toOwnerId) || string.IsNullOrWhiteSpace(resourceId) || quantity <= 0 ||
                fromOwnerId == toOwnerId) return false;
            var receipt = new TransactionReceipt { TransactionId = transactionId, Kind = "Transfer",
                FromOwnerId = fromOwnerId, ToOwnerId = toOwnerId, ResourceId = resourceId, Quantity = quantity };
            if (committedTransactions.TryGetValue(transactionId, out var committed)) return committed.Matches(receipt);
            if (Balance(fromOwnerId, resourceId) < quantity) return false;
            int newTo;
            try { newTo = checked(Balance(toOwnerId, resourceId) + quantity); }
            catch (OverflowException) { return false; }
            balances[Key(fromOwnerId, resourceId)] -= quantity;
            balances[Key(toOwnerId, resourceId)] = newTo;
            committedTransactions.Add(transactionId, receipt);
            Ledger.Add(new LedgerEntry { Id = world.NextId(), TransactionId = transactionId,
                FromOwnerId = fromOwnerId, ToOwnerId = toOwnerId, ResourceId = resourceId, Quantity = quantity, Time = world.Time });
            world.Emit("economy.transferred", fromOwnerId, toOwnerId, resourceId + ":" + quantity);
            return true;
        }

        public bool Exchange(string transactionId, string buyerId, string sellerId, string itemId, int itemCount, string moneyId, int price)
        {
            if (string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(buyerId) ||
                string.IsNullOrWhiteSpace(sellerId) || string.IsNullOrWhiteSpace(itemId) ||
                string.IsNullOrWhiteSpace(moneyId) || itemCount <= 0 || price < 0 ||
                buyerId == sellerId || itemId == moneyId) return false;
            var receipt = new TransactionReceipt { TransactionId = transactionId, Kind = "Exchange",
                FromOwnerId = buyerId, ToOwnerId = sellerId, ResourceId = itemId, Quantity = itemCount,
                CounterResourceId = moneyId, CounterQuantity = price };
            if (committedTransactions.TryGetValue(transactionId, out var committed)) return committed.Matches(receipt);
            if (Balance(sellerId, itemId) < itemCount || Balance(buyerId, moneyId) < price) return false;
            int buyerItems, sellerMoney;
            try { buyerItems = checked(Balance(buyerId, itemId) + itemCount); sellerMoney = checked(Balance(sellerId, moneyId) + price); }
            catch (OverflowException) { return false; }
            balances[Key(sellerId, itemId)] -= itemCount;
            balances[Key(buyerId, itemId)] = buyerItems;
            balances[Key(buyerId, moneyId)] -= price;
            balances[Key(sellerId, moneyId)] = sellerMoney;
            committedTransactions.Add(transactionId, receipt);
            Ledger.Add(new LedgerEntry { Id = world.NextId(), TransactionId = transactionId,
                FromOwnerId = sellerId, ToOwnerId = buyerId, ResourceId = itemId, Quantity = itemCount, Time = world.Time });
            if (price > 0) Ledger.Add(new LedgerEntry { Id = world.NextId(), TransactionId = transactionId,
                FromOwnerId = buyerId, ToOwnerId = sellerId, ResourceId = moneyId, Quantity = price, Time = world.Time });
            world.Emit("economy.exchanged", sellerId, buyerId, transactionId);
            return true;
        }

        public Dictionary<string, int> CopyBalances() { return new Dictionary<string, int>(balances, StringComparer.Ordinal); }
        public List<string> CopyTransactionIds() { return new List<string>(committedTransactions.Keys); }
        public List<TransactionReceipt> CopyTransactionReceipts()
        {
            var result = new List<TransactionReceipt>();
            foreach (var receipt in committedTransactions.Values) result.Add(SnapshotClone.Copy(receipt));
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.TransactionId, b.TransactionId));
            return result;
        }
        internal void Restore(Dictionary<string, int> restored, IEnumerable<TransactionReceipt> receipts)
        {
            balances.Clear(); committedTransactions.Clear();
            foreach (var pair in restored) balances.Add(pair.Key, pair.Value);
            foreach (var receipt in receipts)
            {
                if (receipt == null || string.IsNullOrWhiteSpace(receipt.TransactionId) ||
                    (receipt.Kind != "Transfer" && receipt.Kind != "Exchange"))
                    throw new InvalidOperationException("Invalid committed transaction receipt.");
                committedTransactions.Add(receipt.TransactionId, SnapshotClone.Copy(receipt));
            }
        }
    }
}
