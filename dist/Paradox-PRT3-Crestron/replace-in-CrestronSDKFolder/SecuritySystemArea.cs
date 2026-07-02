// For Basic SIMPL# Classes
// For Basic SIMPL#Pro classes

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Events;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.SecuritySystem;
using Crestron.SimplSharp;

namespace SecuritySystem_Crestron_SampleDriverModel_IP
{
	/// <summary>
	/// This class is used to implement the security system's area interface
	/// </summary>
	public class SecuritySystemArea : ISecuritySystemArea, IDisposable
	{
		#region Fields

		private readonly List<SecuritySystemAreaCommand> _availableAreaCommands;
		private readonly ReadOnlyCollection<SecuritySystemState> _supportedArmingStates;
		private readonly ReadOnlyCollection<SecuritySystemAlarmType> _supportedAlarmTypes;
		private List<SecuritySystemState> _activeAreaStates;
		private List<SecuritySystemAlarmType> _activeAreaAlarms;
		private Dictionary<int, ISecuritySystemZone> _zonesInArea;
		private readonly SecuritySystemProtocol _securitySystemProtocol;
		private bool _disposed = false;
		private string _name;
		private const string PASSWORD = "1234";

		#endregion

		#region Ctor

		public SecuritySystemArea(string name, int index,
			ReadOnlyCollection<SecuritySystemState> supportedArmingStates,
			ReadOnlyCollection<SecuritySystemAlarmType> supportedAlarmTypes,
			ReadOnlyCollection<SecuritySystemAreaCommand> areaCommands,
		   SecuritySystemProtocol protocol)
		{
			_activeAreaStates = new List<SecuritySystemState>();
			_activeAreaAlarms = new List<SecuritySystemAlarmType>();
			_securitySystemProtocol = protocol;
			_supportedArmingStates = supportedArmingStates;
			_availableAreaCommands = areaCommands.ToList();
			_supportedAlarmTypes = supportedAlarmTypes;
			Name = name;
			Index = index;
			_zonesInArea = new Dictionary<int, ISecuritySystemZone>();
			_securitySystemProtocol = protocol;
			_securitySystemProtocol.ZoneListChanged += OnProtocolZoneListChanged;
			_securitySystemProtocol.KeypadChange += OnProtocolKeypadChanged;
			_securitySystemProtocol.KeypadAlarmChange += OnProtocolAlarmChanged;
			CrestronConsole.PrintLine("SecuritySystemArea : Ctor : Called ");
		}

		#endregion

		#region ISecuritySystemArea Implementation

		public int Index { get; set; }

		public string Name
		{
			get
			{
				return _name;
			}
			set
			{
				_name = value;
				RaiseNameChangedEvent();
			}
		}
		public event EventHandler<ValueEventArgs<string>> NameChanged;
		public event EventHandler<ListChangedEventArgs<SecuritySystemAlarmType>> SecuritysystemAlarmStateChangedEvent;
		public event EventHandler<ListChangedEventArgs<SecuritySystemState>> SecuritysystemAreaStateChangedEvent;
		public event EventHandler<ListChangedEventArgs<ISecuritySystemZone>> ZoneListChangedEvent;

		public IEnumerable<SecuritySystemAlarmType> GetActiveAlarms()
		{
			return _activeAreaAlarms;
		}

		public IEnumerable<SecuritySystemState> GetActiveStates()
		{
			return _activeAreaStates;
		}

		public ReadOnlyCollection<SecuritySystemAlarmType> GetSupportedAlarmTypes()
		{
			return _supportedAlarmTypes;
		}

		public ReadOnlyCollection<SecuritySystemState> GetSupportedArmingStates()
		{
			return _supportedArmingStates;
		}

		public IEnumerable<KeyValuePair<int, ISecuritySystemZone>> GetZones()
		{
			return _zonesInArea;
		}

		public SecuritySystemOperationalResult SendAreaCommand(int commandIndex, string password)
		{
			CrestronConsole.PrintLine("Security system area : SendAreaCommand is called : Command index {0} and password {1} ", commandIndex, password);
			SecuritySystemAreaCommand areaCommand = null;
			if (_availableAreaCommands != null)
			{
				foreach (var command in _availableAreaCommands.Where(command => command.Index == commandIndex))
				{
					areaCommand = command;
					break;
				}
			}

			SecuritySystemOperationalResult result = null;

			if (areaCommand == null)
			{
				CrestronConsole.PrintLine("Security system area : SendAreaCommand is called :  areaCommand is null");
				result = new SecuritySystemOperationalResult(0);
				result.Result = SecuritySystemOperationalResultCode.InvalidIdParameters;
				return result;
			}

			if (areaCommand.PasswordRequired && password != PASSWORD)
			{
				CrestronConsole.PrintLine("Security syste marea : SendAreaCommand is called : Send Area command failed due to Invalid Passcode.");
				result = new SecuritySystemOperationalResult(0);
				result.Result = SecuritySystemOperationalResultCode.InvalidPasscode;
				return result;
			}

			var areaIndexes = new List<int>() { this.Index };

			result = _securitySystemProtocol.ExecuteSecurityCommands(commandIndex, areaIndexes, password);
			//this logic would normally in the feedback when the system is already armed/disarmed but putting here to fake fb logic for sample
			if (result.Result != SecuritySystemOperationalResultCode.Success)
			{
				CrestronConsole.PrintLine("Security system area : SendAreaCommand is called :  ExecuteSecurityCommands failed: {0}", result.Result);
				return result;
			}

			CrestronConsole.PrintLine("Security system area : SendAreaCommand is called : Area command command type {0} ", areaCommand.CommandType);
			UpdateArmingState(areaCommand.CommandType);
			return result;
		}

		#endregion

		// ============================================================
		//  Paradox PRT3 — נקודות עדכון חיצוניות מהמנוע
		// ============================================================
		/// <summary>קובע מצב דריכה לפי סוג פקודה (Away/Stay/Disarm).</summary>
		public void SetArmedMode(SecuritySystemCommandType commandType)
		{
			UpdateArmingState(commandType);
		}

		/// <summary>מסמן/מסיר אזעקה על המחיצה.</summary>
		public void SetAlarmExternal(SecuritySystemAlarmType type, bool active)
		{
			UpdateAlarmState(type, active);
		}

		#region Private Method

		private void UpdateArmingState(SecuritySystemCommandType commandType)
		{
			switch (commandType)
			{
				case SecuritySystemCommandType.Disarm:
					UpdateArmingState(SecuritySystemState.ArmedAway, false);
					UpdateArmingState(SecuritySystemState.ArmedStay, false);
					UpdateArmingState(SecuritySystemState.Disarmed, true);
					break;
				case SecuritySystemCommandType.ForceAway:
				case SecuritySystemCommandType.Away:
					UpdateArmingState(SecuritySystemState.ArmedStay, false);
					UpdateArmingState(SecuritySystemState.Disarmed, false);
					UpdateArmingState(SecuritySystemState.ArmedAway, true);
					break;
				case SecuritySystemCommandType.ForceStay:
				case SecuritySystemCommandType.Stay:
					UpdateArmingState(SecuritySystemState.Disarmed, false);
					UpdateArmingState(SecuritySystemState.ArmedAway, false);
					UpdateArmingState(SecuritySystemState.ArmedStay, true);
					break;
			}
		}

		private void UpdateArmingState(SecuritySystemState state, bool active)
		{
			CrestronConsole.PrintLine("Security system area : UpdateArmingState is called for type - {0}", state);
			ListChangedEventArgs<SecuritySystemState> e = null;
			if (active)
			{
				if (!_activeAreaStates.Contains(state))
				{
					int count = _activeAreaStates.Count;
					_activeAreaStates.Add(state);
					e = new ListChangedEventArgs<SecuritySystemState>(ListChangedAction.Added, SecuritySystemState.Unknown, state, count);
				}
			}
			else
			{
				if (_activeAreaStates.Contains(state))
				{
					int index = _activeAreaStates.IndexOf(state);
					if (index >= 0)
					{
						_activeAreaStates.Remove(state);
						e = new ListChangedEventArgs<SecuritySystemState>(ListChangedAction.Removed, state, SecuritySystemState.Unknown, index);
					}
				}
			}

			if (e != null)
			{
				RaiseStateChangedEvent(e);
			}
		}

		private void UpdateAlarmState(SecuritySystemAlarmType type, bool active)
		{
			int index;
			ListChangedEventArgs<SecuritySystemAlarmType> ListChangedEventArgs = null;
			if (active)
			{
				if (!_activeAreaAlarms.Contains(type))
				{
					index = _activeAreaAlarms.Count;
					_activeAreaAlarms.Add(type);
					ListChangedEventArgs = new ListChangedEventArgs<SecuritySystemAlarmType>(ListChangedAction.Added, SecuritySystemAlarmType.Unknown, type, index);
				}
			}
			else
			{
				if (_activeAreaAlarms.Contains(type))
				{
					index = _activeAreaAlarms.IndexOf(type);
					if (index >= 0)
					{
						_activeAreaAlarms.Remove(type);
						ListChangedEventArgs = new ListChangedEventArgs<SecuritySystemAlarmType>(ListChangedAction.Removed, type, SecuritySystemAlarmType.Unknown, index);
					}
				}
			}

			if (ListChangedEventArgs != null)
			{
				RaiseAlarmStateChangedEvent(ListChangedEventArgs);
			}
		}

		private void RaiseNameChangedEvent()
		{
			var handler = NameChanged;
			if (handler != null)
			{
				handler(this, new ValueEventArgs<string>(_name));
				CrestronConsole.PrintLine("Security system area : RaiseNameChangedEvent : Raised NameChanged event ");
			}
			else
			{
				CrestronConsole.PrintLine("Security system area : RaiseNameChangedEvent :  NameChanged event is null");
			}
		}

		private void RaiseStateChangedEvent(ListChangedEventArgs<SecuritySystemState> e)
		{
			var handler = SecuritysystemAreaStateChangedEvent;
			if (handler != null)
			{
				CrestronConsole.PrintLine("Security system area : RaiseStateChangedEvent : Raised SecuritysystemAreaStateChangedEvent event ");
				handler(this, e);
			}
			else
			{
				CrestronConsole.PrintLine("Security system area : RaiseStateChangedEvent : SecuritysystemAreaStateChangedEvent event is null");
			}
		}

		private void RaiseAlarmStateChangedEvent(ListChangedEventArgs<SecuritySystemAlarmType> e)
		{
			var handler = SecuritysystemAlarmStateChangedEvent;
			if (handler != null)
			{
				CrestronConsole.PrintLine("Security system area : RaiseAlarmStateChangedEvent : Raised SecuritysystemAlarmStateChangedEvent event ");
				handler(this, e);
			}
			else
			{
				CrestronConsole.PrintLine("Security system area : RaiseAlarmStateChangedEvent : Raised SecuritysystemAlarmStateChangedEvent event ");
			}
		}

		private void OnProtocolZoneListChanged(object sender, ListChangedEventArgs<ISecuritySystemZone> e)
		{
			CrestronConsole.PrintLine("SecuritySystemArea : OnProtocolZoneListChanged is Called ");

			switch (e.ChangedAction)
			{
				case ListChangedAction.Added:
					if (!_zonesInArea.ContainsKey(e.NewItem.Index))
					{
						_zonesInArea.Add(e.NewItem.Index, e.NewItem);
						var handler = ZoneListChangedEvent;
						if (handler != null)
						{
							handler(this, new ListChangedEventArgs<ISecuritySystemZone>(ListChangedAction.Added, null, e.NewItem, e.Index));
							CrestronConsole.PrintLine("SecuritySystemArea : OnProtocolZoneListChanged : Raised ZoneListChangedEvent event");
						}
						else
						{
							CrestronConsole.PrintLine("SecuritySystemArea : OnProtocolZoneListChanged : ZoneListChangedEvent is null");
						}
					}
					break;

				case ListChangedAction.Removed:
					if (_zonesInArea.ContainsKey(e.OldItem.Index))
					{
						_zonesInArea.Remove(e.OldItem.Index);
						var handler = ZoneListChangedEvent;
						if (handler != null)
						{
							handler(this, new ListChangedEventArgs<ISecuritySystemZone>(ListChangedAction.Removed, e.OldItem, null, Index));
							CrestronConsole.PrintLine("SecuritySystemArea : OnProtocolZoneListChanged : Raised ZoneListChangedEvent event");
						}
						else
						{
							CrestronConsole.PrintLine("SecuritySystemArea : OnProtocolZoneListChanged : ZoneListChangedEvent is null");
						}
					}
					break;
			}
		}

		private void OnProtocolKeypadChanged(object changedObject)
		{
			var obj = (SecuritySystemStateArgs)changedObject;
			if (obj != null)
			{
				switch (obj.EventType)
				{
					case SecuritySystemState.ArmedAway:
						UpdateArmingState(SecuritySystemCommandType.Away);
						break;
					case SecuritySystemState.ArmedStay:
						UpdateArmingState(SecuritySystemCommandType.Stay);
						break;
					case SecuritySystemState.Disarmed:
						UpdateArmingState(SecuritySystemCommandType.Disarm);
						break;
				}
			}
		}

		private void OnProtocolAlarmChanged(object changedObject)
		{
			var obj = (SecuritySystemAlarmStateArgs)changedObject;
			if (obj != null)
			{
				UpdateAlarmState(obj.Alarm.AlarmType, obj.State);
			}
		}

		#endregion

		#region IDisposable Members

		/// <summary>
		/// Dispose
		/// </summary>
		public void Dispose()
		{
			if (!_disposed)
			{
				if (_securitySystemProtocol != null)
				{
					_securitySystemProtocol.ZoneListChanged -= OnProtocolZoneListChanged;
					_securitySystemProtocol.KeypadChange -= OnProtocolKeypadChanged;
					_securitySystemProtocol.KeypadAlarmChange -= OnProtocolAlarmChanged;
				}

				_disposed = true;
			}
		}
		#endregion
	}
}
