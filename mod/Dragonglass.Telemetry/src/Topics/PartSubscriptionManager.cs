// Translates SubscriptionBus events for `part/<id>` topics into
// AddComponent / Destroy calls on the matching Part.
//
// No internal bookkeeping: a PartTopic is a MonoBehaviour attached to
// the Part's own GameObject, so the "is there already a sampler for
// this part?" question is answered by GetComponent<PartTopic>() on
// the Part, and the PartTopic's lifetime is naturally tied to the
// Part's — stage-off / explode / unload all destroy the PartTopic for
// free.
//
// Always-on. The component lives on the persistent telemetry host,
// so it handles subscribe / unsubscribe signals regardless of scene.
// In flight, parts live in FlightGlobals.PersistentLoadedPartIds; in
// the VAB/SPH editor, in EditorLogic.fetch.ship.parts. We walk both,
// so the same `part/<id>` topic resolves in either context.

using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class PartSubscriptionManager : MonoBehaviour
    {
        private const string PartPrefix = "part/";

        private void OnEnable()
        {
            SubscriptionBus.SubscribeRequested += OnSubscribe;
            SubscriptionBus.UnsubscribeRequested += OnUnsubscribe;
        }

        private void OnDisable()
        {
            SubscriptionBus.SubscribeRequested -= OnSubscribe;
            SubscriptionBus.UnsubscribeRequested -= OnUnsubscribe;
        }

        private void OnSubscribe(string topicName)
        {
            if (!topicName.StartsWith(PartPrefix, System.StringComparison.Ordinal)) return;
            if (!TryResolvePart(topicName, out Part part)) return;
            if (part.gameObject.GetComponent<PartTopic>() != null) return;
            part.gameObject.AddComponent<PartTopic>();
        }

        private void OnUnsubscribe(string topicName)
        {
            if (!topicName.StartsWith(PartPrefix, System.StringComparison.Ordinal)) return;
            if (!TryResolvePart(topicName, out Part part)) return;
            PartTopic pt = part.gameObject.GetComponent<PartTopic>();
            if (pt != null) Destroy(pt);
        }

        private static bool TryResolvePart(string topicName, out Part part)
        {
            part = null;
            if (topicName == null || !topicName.StartsWith(PartPrefix, System.StringComparison.Ordinal))
                return false;
            if (!uint.TryParse(topicName.Substring(PartPrefix.Length), out uint id))
                return false;

            // Flight path: the authoritative loaded-part map.
            if (FlightGlobals.PersistentLoadedPartIds != null
                && FlightGlobals.PersistentLoadedPartIds.TryGetValue(id, out part)
                && part != null)
            {
                return true;
            }

            // Editor path: scan the ship under construction. Editor
            // parts aren't registered in PersistentLoadedPartIds
            // (stock reserves that dictionary for in-flight parts).
            if (HighLogic.LoadedScene == GameScenes.EDITOR
                && EditorLogic.fetch != null
                && EditorLogic.fetch.ship != null)
            {
                ShipConstruct ship = EditorLogic.fetch.ship;
                for (int i = 0; i < ship.parts.Count; i++)
                {
                    Part p = ship.parts[i];
                    if (p != null && p.persistentId == id)
                    {
                        part = p;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
