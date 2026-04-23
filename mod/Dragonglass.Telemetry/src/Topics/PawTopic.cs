// Event-only topic carrying right-click "open this PAW" pulses from
// KSP to the UI.
//
// Every time the pilot right-clicks a Part, the Harmony patch on
// UIPartActionController.SelectPart raises PawBus → PawTopic.OnRaise
// → MarkDirty → the next broadcaster flush emits
// `{"topic":"paw","data":[persistentId]}`.
//
// IsEvent = true so the broadcaster does not cache the pulse for
// snapshot replay — a reconnecting client shouldn't re-open a PAW
// the pilot dismissed minutes ago.
//
// Which parts are actually sampled is *not* handled here — subscribing
// to `part/<id>` on the wire is a transport-level signal (see
// OpDispatcher) that spins up a PartTopic via PartSubscriptionManager.
// This topic is purely the fan-out for the right-click pulse.
//
// Wire format (positional):
//   data: [persistentId]
//     persistentId : string, KSP Part.persistentId in decimal.

using System.Text;
using Dragonglass.Telemetry.Util;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class PawTopic : Topic
    {
        public override string Name { get { return "paw"; } }
        public override bool IsEvent { get { return true; } }

        private bool _hasPendingEvent;
        private uint _pendingPersistentId;

        protected override void OnEnable()
        {
            base.OnEnable();
            PawBus.PartRightClicked += OnPartRightClicked;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            PawBus.PartRightClicked -= OnPartRightClicked;
            _hasPendingEvent = false;
        }

        private void OnPartRightClicked(uint persistentId)
        {
            _pendingPersistentId = persistentId;
            _hasPendingEvent = true;
            MarkDirty();
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            if (_hasPendingEvent)
            {
                Json.WriteString(sb, _pendingPersistentId.ToString());
            }
            sb.Append(']');
            // Clear the pending pulse so the next flush emits only if
            // another right-click lands. (ClearDirty is called
            // externally by the broadcaster.)
            _hasPendingEvent = false;
        }
    }
}
