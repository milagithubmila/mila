using System;
using System.Collections.Generic;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Events;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.SecuritySystem;
using Crestron.SimplSharp;
using System.Collections.ObjectModel;


namespace SecuritySystem_Crestron_SampleDriverModel_IP
{
	/// <summary>
	/// This is used to define security system zone
	/// </summary>
	public class SecuritySystemZone : ISecuritySystemZone4
	{
		#region Fields

		private List<SecuritySystemZoneState> _activeZoneState;
		private const string PASSWORD = "1234";
		private string _name;
		private static readonly List<SecuritySystemZoneState> _supportedStates = new List<SecuritySystemZoneState>()
		{
			SecuritySystemZoneState.Bypassed,
			SecuritySystemZoneState.Faulted,
			SecuritySystemZoneState.LowBattery,
			SecuritySystemZoneState.Tamper
		};

		#endregion

		#region Ctor

		public SecuritySystemZone(string name, int index, int areaIndex)
		{
			Name = name;
			Index = index;
			AreaIndex = areaIndex;
			_activeZoneState = new List<SecuritySystemZoneState>();
			CrestronConsole.PrintLine("SecuritySystemZone : Ctor : Called ");
		}

		#endregion

		#region ISecuritySystemZone Implementation

		public bool SupportsPoll { get; private set; }

		public IEnumerable<SecuritySystemZoneState> GetSupportedStates()
		{
			return _supportedStates;
		}

		public IEnumerable<SecuritySystemZoneState> GetActiveStates()
		{
			return _activeZoneState;
		}

		public SecuritySystemOperationalResult BypassZone(string password)
		{
			CrestronConsole.PrintLine("SecuritySystemZone: BypassZone is called");
			return BypassZone(password, "BypassZone", true, SecuritySystemZoneState.Bypassed);
		}

		public SecuritySystemOperationalResult UnbypassZone(string password)
		{
			CrestronConsole.PrintLine("SecuritySystemZone: UnbypassZone is called");
			return BypassZone(password, "UnbypassZone", false, SecuritySystemZoneState.Bypassed);
		}

		public SecuritySystemOperationalResult Poll()
		{
			CrestronConsole.PrintLine("SecuritySystemZone: Poll is called");
			return new SecuritySystemOperationalResult(0);
		}

		public string Name
		{
			get { return _name; }
			set
			{
				_name = value;
				if (NameChanged != null)
				{
					NameChanged(this, new ValueEventArgs<string>(_name));
				}
			}
		}
		public int Index { get; protected set; }
		public int AreaIndex { get; private set; }
		public SecuritySystemZoneType Type { get; protected set; }
		public event EventHandler<ListChangedEventArgs<SecuritySystemZoneState>> SecuritySystemZoneStateChanged;
		public event EventHandler<ValueEventArgs<string>> NameChanged;
		public bool SupportsBypassZone { get { return true; } }

		#endregion

		// ============================================================
		//  Paradox PRT3 — נקודת עדכון חיצונית מהמנוע
		//  (Faulted = תנועה/פתוח)
		// ============================================================
		public void SetActiveState(SecuritySystemZoneState state, bool active)
		{
			UpdateActiveState(state, active);
		}

		#region Private Method

		private void UpdateActiveState(SecuritySystemZoneState state, bool active)
		{
			CrestronConsole.PrintLine("SecuritySystemZone: UpdateActiveState is called for state - {0}", state);
			ListChangedEventArgs<SecuritySystemZoneState> e = null;
			if (active)
			{
				if (!_activeZoneState.Contains(state))
				{
					int count = _activeZoneState.Count;
					_activeZoneState.Add(state);
					e = new ListChangedEventArgs<SecuritySystemZoneState>(ListChangedAction.Added, SecuritySystemZoneState.Unknown, state, count);
				}
			}
			else
			{
				if (_activeZoneState.Contains(state) && !active)
				{
					int index = _activeZoneState.IndexOf(state);
					if (index >= 0)
					{
						_activeZoneState.Remove(state);
						e = new ListChangedEventArgs<SecuritySystemZoneState>(ListChangedAction.Removed, state, SecuritySystemZoneState.Unknown, index);
					}
				}
			}

			if (e != null)
			{
				var handler = SecuritySystemZoneStateChanged;
				if (handler != null)
				{
					CrestronConsole.PrintLine("SecuritySystemZone : UpdateActiveState : Raised SecuritySystemZoneStateChanged event ");
					handler(this, e);
				}
				else
				{
					CrestronConsole.PrintLine("SecuritySystemZone : UpdateActiveState : SecuritySystemZoneStateChanged event is null ");
				}
			}
		}

		private SecuritySystemOperationalResult BypassZone(string password, string method, bool active, SecuritySystemZoneState state)
		{
			if (password != PASSWORD)
			{
				CrestronConsole.PrintLine("SecuritySystemZone : {0} is called : failed due to Invalid Passcode.", method);
				var result = new SecuritySystemOperationalResult(0);
				result.Result = SecuritySystemOperationalResultCode.InvalidPasscode;
				return result;
			}

			UpdateActiveState(state, active);
			return new SecuritySystemOperationalResult(1) { Result = SecuritySystemOperationalResultCode.Success };
		}

		#endregion
	}
}
