using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    public enum KnowledgeAudience { Public, CollectiveMembers, AssignedRoles }
    public enum KnowledgeOrigin { DirectObservation, Announcement, SystemNotice }
    public enum KnowledgeDelivery { Learned, Updated, Unchanged, Unknown, Denied, Expired, Capacity, Cooldown, Invalid }

    [Serializable]
    public sealed class KnowledgeChannel
    {
        public string Id;
        public string CollectiveId;
        public KnowledgeAudience Audience;
        public string RequiredRoleId;
        public string ObjectScopeId;
        public string PublisherAuthorityAction;
        public bool RequiresSamePlace;
        public bool AllowRelay = true;
        public bool Active = true;
        public double RelayCooldownSeconds = 60;
        public double RetentionSeconds = 86400;
        public double ConfidenceHalfLifeSeconds = 3600;
        public float TransmissionConfidence = 0.9f;
    }

    /// <summary>A personal statement about an event, never proof that the statement is objectively true.</summary>
    [Serializable]
    public sealed class KnowledgeRecord
    {
        public string OriginEventId;
        public string Topic;
        public string SubjectId;
        public string Value;
        public string OriginSourceId;
        public string LastSourceId;
        public string OriginChannelId;
        public string ChannelId;
        public string PlaceId;
        public KnowledgeOrigin Origin;
        public bool DirectlyObserved;
        public double OccurredAt;
        public double LearnedAt;
        public double DecayFromTime;
        public double ExpiresAt;
        public double ConfidenceHalfLifeSeconds;
        public float Confidence;
        public float EmotionalWeight;
        public bool Protected;
        public int RelayDepth;

        public float ConfidenceAt(double time)
        {
            if (!SimulationWorld.Finite(time) || time < DecayFromTime) return 0;
            if (ConfidenceHalfLifeSeconds <= 0) return Confidence;
            return (float)(Confidence * Math.Pow(0.5, (time - DecayFromTime) / ConfidenceHalfLifeSeconds));
        }
    }

    [Serializable]
    public sealed class AgentKnowledgeState
    {
        public string AgentId;
        public List<KnowledgeRecord> Records = new List<KnowledgeRecord>();
    }

    [Serializable]
    public sealed class KnowledgeRelayCooldown
    {
        public string SenderId;
        public string RecipientId;
        public string OriginEventId;
        public double Until;
    }

    [Serializable]
    public sealed class KnowledgeSnapshot
    {
        public int SchemaVersion = 1;
        public int CapacityPerAgent = 64;
        public int MaximumCooldownsPerAgent = 256;
        public int MaximumRelayDepth = 8;
        public float ForgetBelowConfidence = 0.05f;
        public List<KnowledgeChannel> Channels = new List<KnowledgeChannel>();
        public List<AgentKnowledgeState> Agents = new List<AgentKnowledgeState>();
        public List<KnowledgeRelayCooldown> Cooldowns = new List<KnowledgeRelayCooldown>();
    }

    public sealed class KnowledgeAnnouncement
    {
        public string OriginEventId { get; internal set; }
        public int LearnedCount { get; internal set; }
        public int RejectedCount { get; internal set; }
    }

    /// <summary>Inject this personal view into NPC decisions instead of giving them the whole world.</summary>
    public sealed class ObserverKnowledge
    {
        private readonly KnowledgeService service;
        public string AgentId { get; private set; }
        internal ObserverKnowledge(KnowledgeService service, string agentId)
        { this.service = service; AgentId = agentId; }
        public bool TryGet(string originEventId, out KnowledgeRecord record)
        { return service.TryGet(AgentId, originEventId, out record); }
        public IReadOnlyList<KnowledgeRecord> Query(string topic = null, string subjectId = null)
        { return service.Query(AgentId, topic, subjectId); }
    }

    /// <summary>
    /// Explicit information delivery with bounded personal storage. It never subscribes all NPCs to the
    /// world event bus, promotes reports to evidence, or treats unknown information as false world state.
    /// </summary>
    public sealed class KnowledgeService
    {
        private readonly SimulationWorld world;
        private readonly Dictionary<string, KnowledgeChannel> channels = new Dictionary<string, KnowledgeChannel>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<KnowledgeRecord>> records = new Dictionary<string, List<KnowledgeRecord>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<KnowledgeRelayCooldown>> cooldowns = new Dictionary<string, List<KnowledgeRelayCooldown>>(StringComparer.Ordinal);

        public int CapacityPerAgent { get; set; } = 64;
        public int MaximumCooldownsPerAgent { get; set; } = 256;
        public int MaximumRelayDepth { get; set; } = 8;
        public float ForgetBelowConfidence { get; set; } = 0.05f;

        public KnowledgeService(SimulationWorld world)
        { this.world = world ?? throw new ArgumentNullException(nameof(world)); }

        public ObserverKnowledge ForAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId) || !world.Agents.ContainsKey(agentId))
                throw new ArgumentException("Personal knowledge requires a live agent.", nameof(agentId));
            return new ObserverKnowledge(this, agentId);
        }

        public void RegisterChannel(KnowledgeChannel channel)
        {
            ValidateChannel(channel);
            if (channels.ContainsKey(channel.Id)) throw new InvalidOperationException("Duplicate knowledge channel: " + channel.Id);
            channels.Add(channel.Id, SnapshotClone.Copy(channel));
        }

        public bool SetChannelActive(string channelId, bool active)
        {
            if (!channels.TryGetValue(channelId ?? string.Empty, out var channel)) return false;
            channel.Active = active;
            return true;
        }

        public bool TryGetChannel(string channelId, out KnowledgeChannel channel)
        {
            bool found = channels.TryGetValue(channelId ?? string.Empty, out var stored);
            channel = found ? SnapshotClone.Copy(stored) : null;
            return found;
        }

        public bool CanReceive(string agentId, string channelId)
        { return channels.TryGetValue(channelId ?? string.Empty, out var channel) && CanUse(agentId, channel); }

        /// <summary>
        /// The sensing adapter must establish visibility/hearing. Core also checks the canonical event,
        /// logical place, and a current tick window, so historical/global events cannot be observed later.
        /// </summary>
        public KnowledgeDelivery Observe(string observerId, WorldEvent observedEvent, float confidence = 1,
            double retentionSeconds = 86400, double halfLifeSeconds = 3600, bool isProtected = false,
            float emotionalWeight = 0)
        {
            ValidateSettings();
            if (!ValidConfidence(confidence) || !ValidDuration(retentionSeconds) || !ValidHalfLife(halfLifeSeconds) ||
                !SimulationWorld.Finite(emotionalWeight) || emotionalWeight < -1 || emotionalWeight > 1)
                return KnowledgeDelivery.Invalid;
            if (!world.Agents.TryGetValue(observerId ?? string.Empty, out var observer) || observedEvent == null ||
                string.IsNullOrWhiteSpace(observedEvent.PlaceId) || observer.PlaceId != observedEvent.PlaceId ||
                observedEvent.Time > world.Time || world.Time - observedEvent.Time > world.TickSeconds)
                return KnowledgeDelivery.Denied;
            WorldEvent canonical = null;
            foreach (var item in world.Trace) if (item.Id == observedEvent.Id) { canonical = item; break; }
            if (canonical == null || canonical.Type != observedEvent.Type || canonical.SubjectId != observedEvent.SubjectId ||
                canonical.Payload != observedEvent.Payload || canonical.PlaceId != observedEvent.PlaceId ||
                canonical.Time != observedEvent.Time || canonical.EmitterId != observedEvent.EmitterId)
                return KnowledgeDelivery.Denied;
            double expires = Expiration(retentionSeconds, canonical.Time);
            if (!SimulationWorld.Finite(expires)) return KnowledgeDelivery.Invalid;
            return Store(observerId, new KnowledgeRecord
            {
                OriginEventId = canonical.Id, Topic = canonical.Type, SubjectId = canonical.SubjectId,
                Value = canonical.Payload, OriginSourceId = observerId, LastSourceId = observerId,
                OriginChannelId = "direct.observation", ChannelId = "direct.observation", PlaceId = canonical.PlaceId,
                Origin = KnowledgeOrigin.DirectObservation, DirectlyObserved = true,
                OccurredAt = canonical.Time, LearnedAt = world.Time, DecayFromTime = canonical.Time,
                ExpiresAt = expires, ConfidenceHalfLifeSeconds = halfLifeSeconds, Confidence = confidence,
                Protected = isProtected, EmotionalWeight = emotionalWeight
            });
        }

        /// <summary>
        /// Creates a new statement and delivers it only to the channel's currently authorized audience.
        /// Publishing a statement does not create a physical observation or a norm violation record.
        /// </summary>
        public KnowledgeAnnouncement Announce(string channelId, string publisherId, string topic,
            string subjectId, string value, float confidence = 1, bool isProtected = false)
        {
            ValidateSettings();
            if (string.IsNullOrWhiteSpace(topic) || !ValidConfidence(confidence) ||
                !channels.TryGetValue(channelId ?? string.Empty, out var channel) || !CanPublish(publisherId, channel)) return null;
            double expires = Expiration(channel.RetentionSeconds);
            if (!SimulationWorld.Finite(expires)) return null;
            var source = world.Emit("information.published", publisherId, subjectId, topic, AgentPlace(publisherId));
            if (source == null) return null;
            var record = new KnowledgeRecord
            {
                OriginEventId = source.Id, Topic = topic, SubjectId = subjectId, Value = value,
                OriginSourceId = publisherId, LastSourceId = publisherId, OriginChannelId = channel.Id, ChannelId = channel.Id,
                PlaceId = source.PlaceId, Origin = KnowledgeOrigin.Announcement,
                OccurredAt = world.Time, LearnedAt = world.Time, DecayFromTime = world.Time,
                ExpiresAt = expires, ConfidenceHalfLifeSeconds = channel.ConfidenceHalfLifeSeconds,
                Confidence = confidence, Protected = isProtected
            };
            var result = new KnowledgeAnnouncement { OriginEventId = source.Id };
            var recipients = new List<string>(world.Agents.Keys);
            recipients.Sort(StringComparer.Ordinal);
            foreach (var recipient in recipients)
            {
                if (!CanUse(recipient, channel) || !WithinRange(publisherId, recipient, channel)) continue;
                var copy = SnapshotClone.Copy(record);
                if (recipient != publisherId) copy.Confidence *= channel.TransmissionConfidence;
                var outcome = Store(recipient, copy);
                if (outcome == KnowledgeDelivery.Learned || outcome == KnowledgeDelivery.Updated) result.LearnedCount++;
                else result.RejectedCount++;
            }
            return result;
        }

        /// <summary>
        /// System/record adapter entry, e.g. a norm host that has already verified publication and delivery.
        /// The registered channel still checks current audience, source, publisher authority and distance.
        /// The caller owns the stable origin ID and must retain it when reporting the same underlying event.
        /// </summary>
        public KnowledgeDelivery DeliverSystemNotice(string agentId, string originEventId, string topic,
            string subjectId, string value, string sourceId, string channelId, float confidence = 1,
            double retentionSeconds = 86400, double halfLifeSeconds = 0, bool isProtected = true,
            double? occurredAt = null)
        {
            ValidateSettings();
            double originTime = occurredAt ?? world.Time;
            if (string.IsNullOrWhiteSpace(originEventId) || string.IsNullOrWhiteSpace(topic) || !ValidConfidence(confidence) ||
                !ValidDuration(retentionSeconds) || !ValidHalfLife(halfLifeSeconds) || !SimulationWorld.Finite(originTime) ||
                originTime < 0 || originTime > world.Time) return KnowledgeDelivery.Invalid;
            if (!channels.TryGetValue(channelId ?? string.Empty, out var channel) || !CanUse(agentId, channel) ||
                !CanPublish(sourceId, channel) || !WithinRange(sourceId, agentId, channel)) return KnowledgeDelivery.Denied;
            double expires = Expiration(retentionSeconds, originTime);
            if (!SimulationWorld.Finite(expires)) return KnowledgeDelivery.Invalid;
            return Store(agentId, new KnowledgeRecord
            {
                OriginEventId = originEventId, Topic = topic, SubjectId = subjectId, Value = value,
                OriginSourceId = sourceId, LastSourceId = sourceId, OriginChannelId = channelId, ChannelId = channelId,
                PlaceId = AgentPlace(sourceId), Origin = KnowledgeOrigin.SystemNotice,
                OccurredAt = originTime, LearnedAt = world.Time, DecayFromTime = originTime,
                ExpiresAt = expires, ConfidenceHalfLifeSeconds = halfLifeSeconds,
                Confidence = confidence, Protected = isProtected
            });
        }

        public KnowledgeDelivery Relay(string senderId, string recipientId, string originEventId, string channelId)
        {
            ValidateSettings();
            if (senderId == recipientId || !channels.TryGetValue(channelId ?? string.Empty, out var channel) ||
                !channel.AllowRelay || !CanPublish(senderId, channel) || !CanUse(recipientId, channel) ||
                !WithinRange(senderId, recipientId, channel)) return KnowledgeDelivery.Denied;
            var original = Find(senderId, originEventId);
            if (original == null || Forgotten(original)) return KnowledgeDelivery.Unknown;
            // Normal sharing retains the original audience restriction even through a public intermediary.
            // Intentional disclosure of confidential information needs its own explicit domain action.
            if (original.OriginChannelId != "direct.observation" &&
                (!channels.TryGetValue(original.OriginChannelId, out var originChannel) ||
                 !CanUse(senderId, originChannel) || !CanUse(recipientId, originChannel))) return KnowledgeDelivery.Denied;
            if (original.RelayDepth >= MaximumRelayDepth) return KnowledgeDelivery.Denied;
            PruneCooldowns(senderId);
            if (!cooldowns.TryGetValue(senderId, out var recent))
            { recent = new List<KnowledgeRelayCooldown>(); cooldowns.Add(senderId, recent); }
            foreach (var item in recent)
                if (item.RecipientId == recipientId && item.OriginEventId == originEventId && world.Time < item.Until)
                    return KnowledgeDelivery.Cooldown;
            // Never evict an active cooldown, which would allow an information flood to bypass the limit.
            if (channel.RelayCooldownSeconds > 0 && recent.Count >= MaximumCooldownsPerAgent) return KnowledgeDelivery.Capacity;
            double until = world.Time + channel.RelayCooldownSeconds;
            if (!SimulationWorld.Finite(until)) return KnowledgeDelivery.Invalid;
            var copy = SnapshotClone.Copy(original);
            copy.LastSourceId = senderId;
            copy.ChannelId = channelId;
            copy.LearnedAt = world.Time;
            copy.DirectlyObserved = false;
            copy.RelayDepth++;
            copy.Confidence *= channel.TransmissionConfidence;
            var outcome = Store(recipientId, copy);
            if (channel.RelayCooldownSeconds > 0)
                recent.Add(new KnowledgeRelayCooldown { SenderId = senderId, RecipientId = recipientId,
                    OriginEventId = originEventId, Until = until });
            return outcome;
        }

        public bool TryGet(string agentId, string originEventId, out KnowledgeRecord record)
        {
            var stored = world.Agents.ContainsKey(agentId ?? string.Empty) ? Find(agentId, originEventId) : null;
            record = stored != null && !Forgotten(stored) ? SnapshotClone.Copy(stored) : null;
            return record != null;
        }

        public IReadOnlyList<KnowledgeRecord> Query(string agentId, string topic = null, string subjectId = null)
        {
            var result = new List<KnowledgeRecord>();
            if (!world.Agents.ContainsKey(agentId ?? string.Empty) || !records.TryGetValue(agentId, out var personal)) return result;
            foreach (var item in personal)
                if (!Forgotten(item) && (topic == null || item.Topic == topic) && (subjectId == null || item.SubjectId == subjectId))
                    result.Add(SnapshotClone.Copy(item));
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.OriginEventId, b.OriginEventId));
            return result;
        }

        public void Tick()
        {
            ValidateSettings();
            foreach (var pair in records)
            {
                List<KnowledgeRecord> forgotten = null;
                for (int i = pair.Value.Count - 1; i >= 0; i--)
                {
                    if (!Forgotten(pair.Value[i])) continue;
                    if (forgotten == null) forgotten = new List<KnowledgeRecord>();
                    forgotten.Add(pair.Value[i]);
                    pair.Value.RemoveAt(i);
                }
                if (forgotten == null) continue;
                // Capacity eviction during Store stays silent; Tick-time expiry and decay are
                // real belief changes, so goals that waited for this knowledge may re-evaluate.
                forgotten.Sort((a, b) => StringComparer.Ordinal.Compare(a.OriginEventId, b.OriginEventId));
                foreach (var item in forgotten)
                    world.Emit("knowledge.forgotten", item.LastSourceId, pair.Key,
                        ForgottenPayload(item), AgentPlace(pair.Key));
            }
            foreach (var recent in cooldowns.Values) recent.RemoveAll(item => item.Until <= world.Time);
        }

        /// <summary>Payload of a forgotten record: topic, subject and origin event, separated by US.</summary>
        public static string ForgottenPayload(KnowledgeRecord item)
        { return item.Topic + "\u001f" + (item.SubjectId ?? string.Empty) + "\u001f" + item.OriginEventId; }

        public static bool TryParseForgottenPayload(string payload, out string topic, out string subjectId, out string originEventId)
        {
            topic = subjectId = originEventId = null;
            if (string.IsNullOrEmpty(payload)) return false;
            var parts = payload.Split('\u001f');
            if (parts.Length != 3 || parts[0].Length == 0) return false;
            topic = parts[0];
            subjectId = parts[1].Length == 0 ? null : parts[1];
            originEventId = parts[2];
            return originEventId.Length > 0;
        }

        public void RemoveAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            records.Remove(agentId);
            cooldowns.Remove(agentId);
            foreach (var recent in cooldowns.Values) recent.RemoveAll(item => item.RecipientId == agentId);
            // Other people's memories retain the departed source's stable identity.
        }

        public KnowledgeSnapshot Capture()
        {
            ValidateSettings();
            var snapshot = new KnowledgeSnapshot { CapacityPerAgent = CapacityPerAgent,
                MaximumCooldownsPerAgent = MaximumCooldownsPerAgent, MaximumRelayDepth = MaximumRelayDepth,
                ForgetBelowConfidence = ForgetBelowConfidence };
            var channelIds = new List<string>(channels.Keys); channelIds.Sort(StringComparer.Ordinal);
            foreach (var id in channelIds) snapshot.Channels.Add(SnapshotClone.Copy(channels[id]));
            var agentIds = new List<string>(records.Keys); agentIds.Sort(StringComparer.Ordinal);
            foreach (var id in agentIds)
            {
                var entry = new AgentKnowledgeState { AgentId = id };
                foreach (var item in records[id]) if (!Forgotten(item)) entry.Records.Add(SnapshotClone.Copy(item));
                entry.Records.Sort((a, b) => StringComparer.Ordinal.Compare(a.OriginEventId, b.OriginEventId));
                if (entry.Records.Count > 0) snapshot.Agents.Add(entry);
            }
            agentIds = new List<string>(cooldowns.Keys); agentIds.Sort(StringComparer.Ordinal);
            foreach (var id in agentIds)
                foreach (var item in cooldowns[id]) if (item.Until > world.Time) snapshot.Cooldowns.Add(SnapshotClone.Copy(item));
            return snapshot;
        }

        /// <summary>Validate into temporary containers first. A bad checkpoint cannot partially replace current knowledge.</summary>
        public void Restore(KnowledgeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.SchemaVersion != 1 || snapshot.Channels == null || snapshot.Agents == null || snapshot.Cooldowns == null)
                throw new InvalidOperationException("Unsupported knowledge checkpoint.");
            ValidateSettings(snapshot.CapacityPerAgent, snapshot.MaximumCooldownsPerAgent, snapshot.MaximumRelayDepth, snapshot.ForgetBelowConfidence);
            var restoredChannels = new Dictionary<string, KnowledgeChannel>(StringComparer.Ordinal);
            var restoredRecords = new Dictionary<string, List<KnowledgeRecord>>(StringComparer.Ordinal);
            var restoredCooldowns = new Dictionary<string, List<KnowledgeRelayCooldown>>(StringComparer.Ordinal);
            foreach (var item in snapshot.Channels)
            { ValidateChannel(item); restoredChannels.Add(item.Id, SnapshotClone.Copy(item)); }
            foreach (var agent in snapshot.Agents)
            {
                if (agent == null || string.IsNullOrEmpty(agent.AgentId) || !world.Agents.ContainsKey(agent.AgentId) ||
                    agent.Records == null || agent.Records.Count > snapshot.CapacityPerAgent)
                    throw new InvalidOperationException("Knowledge checkpoint has an invalid personal store.");
                var restored = new List<KnowledgeRecord>();
                var origins = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in agent.Records)
                {
                    if (!ValidRecord(item, world.Time) || !origins.Add(item.OriginEventId) ||
                        (item.ChannelId != "direct.observation" && !restoredChannels.ContainsKey(item.ChannelId)) ||
                        (item.OriginChannelId != "direct.observation" && !restoredChannels.ContainsKey(item.OriginChannelId)))
                        throw new InvalidOperationException("Knowledge checkpoint contains invalid or duplicate information.");
                    restored.Add(SnapshotClone.Copy(item));
                }
                restoredRecords.Add(agent.AgentId, restored);
            }
            foreach (var item in snapshot.Cooldowns)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.OriginEventId) || string.IsNullOrEmpty(item.SenderId) ||
                    string.IsNullOrEmpty(item.RecipientId) || item.SenderId == item.RecipientId || !world.Agents.ContainsKey(item.SenderId) ||
                    !world.Agents.ContainsKey(item.RecipientId) || !SimulationWorld.Finite(item.Until) || item.Until < 0)
                    throw new InvalidOperationException("Knowledge checkpoint has an invalid relay cooldown.");
                if (!restoredCooldowns.TryGetValue(item.SenderId, out var recent))
                { recent = new List<KnowledgeRelayCooldown>(); restoredCooldowns.Add(item.SenderId, recent); }
                foreach (var other in recent)
                    if (other.RecipientId == item.RecipientId && other.OriginEventId == item.OriginEventId)
                        throw new InvalidOperationException("Knowledge checkpoint has duplicate relay cooldowns.");
                if (recent.Count >= snapshot.MaximumCooldownsPerAgent) throw new InvalidOperationException("Knowledge cooldown limit exceeded.");
                recent.Add(SnapshotClone.Copy(item));
            }
            channels.Clear(); foreach (var item in restoredChannels) channels.Add(item.Key, item.Value);
            records.Clear(); foreach (var item in restoredRecords) records.Add(item.Key, item.Value);
            cooldowns.Clear(); foreach (var item in restoredCooldowns) cooldowns.Add(item.Key, item.Value);
            CapacityPerAgent = snapshot.CapacityPerAgent;
            MaximumCooldownsPerAgent = snapshot.MaximumCooldownsPerAgent;
            MaximumRelayDepth = snapshot.MaximumRelayDepth;
            ForgetBelowConfidence = snapshot.ForgetBelowConfidence;
        }

        private KnowledgeDelivery Store(string agentId, KnowledgeRecord incoming)
        {
            if (!world.Agents.ContainsKey(agentId ?? string.Empty) || !ValidRecord(incoming, world.Time)) return KnowledgeDelivery.Invalid;
            if (Forgotten(incoming)) return KnowledgeDelivery.Expired;
            if (!records.TryGetValue(agentId, out var personal))
            { personal = new List<KnowledgeRecord>(); records.Add(agentId, personal); }
            personal.RemoveAll(Forgotten);
            for (int i = 0; i < personal.Count; i++)
            {
                var old = personal[i];
                if (old.OriginEventId != incoming.OriginEventId) continue;
                if (old.Topic != incoming.Topic || !Same(old.SubjectId, incoming.SubjectId) || !Same(old.Value, incoming.Value))
                    return KnowledgeDelivery.Invalid;
                incoming.DecayFromTime = Math.Min(incoming.DecayFromTime, old.DecayFromTime);
                // One underlying event is one item. Relaying it cannot manufacture additional evidence,
                // renew its lifetime, upgrade its protection, or replace a personal direct observation.
                if ((!incoming.DirectlyObserved && old.DirectlyObserved) ||
                    incoming.ConfidenceAt(world.Time) <= old.ConfidenceAt(world.Time)) return KnowledgeDelivery.Unchanged;
                incoming.ExpiresAt = Math.Min(incoming.ExpiresAt, old.ExpiresAt);
                incoming.Protected = old.Protected;
                incoming.OriginChannelId = old.OriginChannelId;
                incoming.OccurredAt = old.OccurredAt;
                incoming.LearnedAt = old.LearnedAt;
                personal[i] = SnapshotClone.Copy(incoming);
                Learned(agentId, incoming, "knowledge.updated");
                return KnowledgeDelivery.Updated;
            }
            while (personal.Count >= CapacityPerAgent)
            {
                int weakest = -1;
                for (int i = 0; i < personal.Count; i++)
                {
                    if (personal[i].Protected) continue;
                    if (weakest < 0 || Salience(personal[i]) < Salience(personal[weakest]) ||
                        (Salience(personal[i]) == Salience(personal[weakest]) &&
                         StringComparer.Ordinal.Compare(personal[i].OriginEventId, personal[weakest].OriginEventId) < 0)) weakest = i;
                }
                if (weakest < 0) return KnowledgeDelivery.Capacity;
                personal.RemoveAt(weakest);
            }
            personal.Add(SnapshotClone.Copy(incoming));
            Learned(agentId, incoming, "knowledge.learned");
            return KnowledgeDelivery.Learned;
        }

        private void Learned(string agentId, KnowledgeRecord item, string type)
        { world.Emit(type, item.LastSourceId, agentId, item.OriginEventId, world.Agents[agentId].PlaceId); }

        private KnowledgeRecord Find(string agentId, string eventId)
        {
            if (!records.TryGetValue(agentId ?? string.Empty, out var personal)) return null;
            foreach (var item in personal) if (item.OriginEventId == eventId) return item;
            return null;
        }

        private bool Forgotten(KnowledgeRecord item)
        { return !item.Protected && (world.Time >= item.ExpiresAt || item.ConfidenceAt(world.Time) < ForgetBelowConfidence); }

        private float Salience(KnowledgeRecord item)
        { return item.ConfidenceAt(world.Time) * (1 + Math.Abs(item.EmotionalWeight)); }

        private bool CanUse(string agentId, KnowledgeChannel channel)
        {
            if (!channel.Active || string.IsNullOrEmpty(agentId) || !world.Agents.ContainsKey(agentId)) return false;
            if (!string.IsNullOrEmpty(channel.CollectiveId) &&
                (!world.Society.Collectives.TryGetValue(channel.CollectiveId, out var collective) || !collective.Active)) return false;
            if (channel.Audience == KnowledgeAudience.Public) return true;
            bool member = false;
            foreach (var membership in world.Society.Memberships.Values)
                if (membership.AgentId == agentId && membership.CollectiveId == channel.CollectiveId && membership.Active &&
                    world.Time >= membership.BeginsAt && world.Time < membership.EndsAt) { member = true; break; }
            if (!member) return false;
            if (channel.Audience == KnowledgeAudience.CollectiveMembers) return true;
            foreach (var role in world.Society.Roles.Values)
                if (role.AgentId == agentId && role.CollectiveId == channel.CollectiveId && role.Active &&
                    world.Time >= role.BeginsAt && world.Time < role.EndsAt &&
                    (string.IsNullOrEmpty(channel.RequiredRoleId) || role.RoleId == channel.RequiredRoleId) &&
                    (string.IsNullOrEmpty(channel.ObjectScopeId) || role.ObjectScopeId == channel.ObjectScopeId) &&
                    role.InformationChannels != null && role.InformationChannels.Contains(channel.Id)) return true;
            return false;
        }

        private bool CanPublish(string sourceId, KnowledgeChannel channel)
        {
            // An organization may publish through its own configured channel; unrelated collectives cannot impersonate it.
            if (!string.IsNullOrEmpty(sourceId) && sourceId == channel.CollectiveId && channel.Active &&
                world.Society.Collectives.TryGetValue(sourceId, out var collective) && collective.Active && !channel.RequiresSamePlace)
                return true;
            if (!CanUse(sourceId, channel)) return false;
            return string.IsNullOrEmpty(channel.PublisherAuthorityAction) ||
                world.Society.HasAuthority(sourceId, channel.PublisherAuthorityAction, channel.CollectiveId, objectId: channel.ObjectScopeId);
        }

        private bool WithinRange(string senderId, string recipientId, KnowledgeChannel channel)
        {
            if (!channel.RequiresSamePlace) return true;
            var place = AgentPlace(senderId);
            return !string.IsNullOrEmpty(place) && place == AgentPlace(recipientId);
        }

        private string AgentPlace(string agentId)
        { return world.Agents.TryGetValue(agentId ?? string.Empty, out var agent) ? agent.PlaceId : null; }

        private void PruneCooldowns(string agentId)
        { if (cooldowns.TryGetValue(agentId, out var recent)) recent.RemoveAll(item => item.Until <= world.Time); }

        private double Expiration(double duration)
        { return duration == double.MaxValue ? double.MaxValue : world.Time + duration; }

        private static double Expiration(double duration, double fromTime)
        { return duration == double.MaxValue ? double.MaxValue : fromTime + duration; }

        private static bool Same(string left, string right)
        { return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal); }

        private static bool ValidConfidence(float value)
        { return SimulationWorld.Finite(value) && value > 0 && value <= 1; }
        private static bool ValidDuration(double value)
        { return SimulationWorld.Finite(value) && value > 0; }
        private static bool ValidHalfLife(double value)
        { return SimulationWorld.Finite(value) && value >= 0; }

        private static bool ValidRecord(KnowledgeRecord item, double now)
        {
            return item != null && !string.IsNullOrWhiteSpace(item.OriginEventId) && !string.IsNullOrWhiteSpace(item.Topic) &&
                !string.IsNullOrWhiteSpace(item.OriginSourceId) && !string.IsNullOrWhiteSpace(item.LastSourceId) &&
                !string.IsNullOrWhiteSpace(item.ChannelId) && !string.IsNullOrWhiteSpace(item.OriginChannelId) &&
                Enum.IsDefined(typeof(KnowledgeOrigin), item.Origin) &&
                SimulationWorld.Finite(item.OccurredAt) && item.OccurredAt >= 0 && item.OccurredAt <= now &&
                SimulationWorld.Finite(item.DecayFromTime) && item.DecayFromTime >= 0 && item.DecayFromTime <= now &&
                SimulationWorld.Finite(item.LearnedAt) && item.LearnedAt >= item.OccurredAt && item.LearnedAt <= now &&
                SimulationWorld.Finite(item.ExpiresAt) && item.ExpiresAt > item.OccurredAt &&
                ValidHalfLife(item.ConfidenceHalfLifeSeconds) && ValidConfidence(item.Confidence) &&
                SimulationWorld.Finite(item.EmotionalWeight) && item.EmotionalWeight >= -1 && item.EmotionalWeight <= 1 && item.RelayDepth >= 0;
        }

        private static void ValidateChannel(KnowledgeChannel channel)
        {
            if (channel == null || string.IsNullOrWhiteSpace(channel.Id) || channel.Id == "direct.observation" ||
                !Enum.IsDefined(typeof(KnowledgeAudience), channel.Audience) ||
                (channel.Audience != KnowledgeAudience.Public && string.IsNullOrWhiteSpace(channel.CollectiveId)) ||
                (!string.IsNullOrEmpty(channel.PublisherAuthorityAction) && string.IsNullOrWhiteSpace(channel.CollectiveId)) ||
                (channel.Audience != KnowledgeAudience.AssignedRoles && !string.IsNullOrEmpty(channel.RequiredRoleId)) ||
                !SimulationWorld.Finite(channel.RelayCooldownSeconds) || channel.RelayCooldownSeconds < 0 ||
                !ValidDuration(channel.RetentionSeconds) || !ValidHalfLife(channel.ConfidenceHalfLifeSeconds) ||
                !ValidConfidence(channel.TransmissionConfidence))
                throw new ArgumentException("Invalid knowledge channel.");
        }

        private void ValidateSettings()
        { ValidateSettings(CapacityPerAgent, MaximumCooldownsPerAgent, MaximumRelayDepth, ForgetBelowConfidence); }

        private static void ValidateSettings(int capacity, int cooldownCapacity, int maxDepth, float threshold)
        {
            if (capacity < 1 || capacity > 4096 || cooldownCapacity < 1 || cooldownCapacity > 16384 || maxDepth < 1 || maxDepth > 64 ||
                !SimulationWorld.Finite(threshold) || threshold < 0 || threshold >= 1)
                throw new InvalidOperationException("Knowledge limits or forgetting threshold are invalid.");
        }
    }
}
