using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Crestron.RAD.Common;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Events;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.SecuritySystem;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Reflection;
using Newtonsoft.Json;

namespace SecuritySystem_Crestron_SampleDriverModel_IP
{
	public class SecuritySystemDriverIP : ABasicDriver,
		ISecuritySystem,
		ITcp
	{
		#region Ctor

		public SecuritySystemDriverIP()
			: base()
		{
			var state = new List<SecuritySystemState>();
			state.Add(SecuritySystemState.ArmedAway);
			state.Add(SecuritySystemState.Disarmed);
			_availableStates = state.AsReadOnly();

			var alarmType = new List<SecuritySystemAlarmType> {
                SecuritySystemAlarmType.Fire,
                SecuritySystemAlarmType.Alarm,
                SecuritySystemAlarmType.Burglary
            };
			_availableAlarms = alarmType.AsReadOnly();

			var areaCommand = new List<SecuritySystemAreaCommand>();
			areaCommand.Add(new SecuritySystemAreaCommand(1, SecuritySystemCommandType.Disarm, true));
			areaCommand.Add(new SecuritySystemAreaCommand(2, SecuritySystemCommandType.Away, true));
			areaCommand.Add(new SecuritySystemAreaCommand(3, SecuritySystemCommandType.Stay, true));
			_availableAreaCommands = areaCommand.AsReadOnly();

			_securitySystemKeypad = new SecuritySystemKeypad();
		}

		#endregion

		#region ITcp Members

		void ITcp.Initialize(Crestron.SimplSharp.IPAddress ipAddress, int port)
		{
			Initialize(ipAddress, port);
		}

		public void Initialize(IPAddress ipAddress, int port)
		{
			_tcpTransport = new SampleTransport
			{
				EnableAutoReconnect = EnableAutoReconnect,
				EnableLogging = InternalEnableLogging,
				CustomLogger = InternalCustomLogger,
				EnableRxDebug = InternalEnableRxDebug,
				EnableTxDebug = InternalEnableTxDebug
			};

			ConnectionTransport = _tcpTransport;
			_securitySystemProtocol = new SecuritySystemProtocol(this, ConnectionTransport, Id)
			{
				EnableLogging = true,
				CustomLogger = InternalCustomLogger,
				EnableStackTrace = true
			};


			DeviceProtocol = _securitySystemProtocol;
			SubscribeToProtocolEvents();
			_tcpTransport.LogTxAndRxAsBytes = true;
			InitializeKeypad();

			// ============================================================
			//  Paradox PRT3 — הפעלת המנוע עם היעד שהוגדר ב-Crestron Home.
			//  מנוע ה-PRT3 פותח בעצמו את החיבור לממיר (host:port).
			// ============================================================
			_securitySystemProtocol.StartEngine(ipAddress.ToString(), port);
		}

		#endregion

		#region Fields

		SecuritySystemProtocol _securitySystemProtocol;
		IEmulatedSecuritySystemKeypad _securitySystemKeypad;
		TcpTransport _tcpTransport;

		List<SecuritySystemError> _currentlyActiveErrors = new List<SecuritySystemError>();

		// The values here determine what features are enabled in Crestron Home
		List<SecuritySystemCapabilities> CapabilityList = new List<SecuritySystemCapabilities>
		{
			SecuritySystemCapabilities.DirectControl, // Used for area support
			SecuritySystemCapabilities.KeypadEmulation, // Use for the virtual keypad
			SecuritySystemCapabilities.Zones // Used for zone support
		};

		List<SecuritySystemAlarmType> _currentlyActiveAlarms = new List<SecuritySystemAlarmType>();
		List<SecuritySystemState> _currentlyActiveStates = new List<SecuritySystemState>();

		ReadOnlyCollection<SecuritySystemAreaCommand> _availableAreaCommands;
		ReadOnlyCollection<SecuritySystemAlarmType> _availableAlarms;
		ReadOnlyCollection<SecuritySystemState> _availableStates;

		#endregion

		#region Property

		#endregion

		#region ISecuritySystem Implementation

		#region Events

		public event EventHandler<ListChangedEventArgs<SecuritySystemAlarmType>> SecuritysystemAlarmStateChangedEvent;
		public event EventHandler<ListChangedEventArgs<ISecuritySystemArea>> SecuritySystemAreaListChanged;
		public event EventHandler<SecuritySystemCommandResultEventArgs> SecuritySystemCommandResult;
		public event EventHandler<ListChangedEventArgs<SecuritySystemError>> SecuritySystemErrorChanged;
		public event EventHandler<ListChangedEventArgs<SecuritySystemState>> SecuritysystemStateChangedEvent;
		public event EventHandler<ListChangedEventArgs<ISecuritySystemZone>> SecuritySystemZoneListChanged;

		#endregion

		/// <summary>
		/// It's Not currently supported
		/// </summary>
		public void BypassZone(int areaIndex, int zoneIndex, string password)
		{
		}

		public IEnumerable<SecuritySystemError> GetActiveErrors()
		{
			return _currentlyActiveErrors;
		}

		public IEnumerable<ISecuritySystemZone> GetAllZones()
		{
			if (_securitySystemProtocol == null)
				return null;
			return _securitySystemProtocol.Zones;
		}

		public ISecuritySystemArea GetArea(int index)
		{
			if (_securitySystemProtocol == null)
				return null;

			var areas = _securitySystemProtocol.Areas;

			for (int i = 0; i < areas.Count; i++)
			{
				if (areas[i].Index == index)
					return areas[i];
			}

			return null;
		}

		public IEnumerable<ISecuritySystemArea> GetAreas()
		{
			if (_securitySystemProtocol == null)
				return null;
			return _securitySystemProtocol.Areas;
		}

		public ReadOnlyCollection<SecuritySystemAreaCommand> GetAvailableAreaCommands()
		{
			return _availableAreaCommands;
		}

		public ReadOnlyCollection<SecuritySystemAlarmType> GetAvailableSystemAlarms()
		{
			return _availableAlarms;
		}

		public ReadOnlyCollection<SecuritySystemState> GetAvailableSystemStates()
		{
			return _availableStates;
		}

		public ReadOnlyCollection<SecuritySystemCapabilities> GetCapabilities()
		{
			return new ReadOnlyCollection<SecuritySystemCapabilities>(CapabilityList);
		}

		public IEnumerable<SecuritySystemAlarmType> GetCurrentSystemAlarms()
		{
			return _currentlyActiveAlarms;
		}

		public IEnumerable<SecuritySystemState> GetCurrentSystemStates()
		{
			return _currentlyActiveStates;
		}

		public IEmulatedSecuritySystemKeypad GetEmulatedKeypad()
		{
			return _securitySystemKeypad;
		}

		public SecuritySystemOperationalResult SendAreaCommand(List<int> areaIndexes, int commandIndex, string password)
		{
			SecuritySystemAreaCommand areaCommand = null;
			SecuritySystemOperationalResult result = null;

			if (_availableAreaCommands != null)
			{
				foreach (var command in _availableAreaCommands)
				{
					if (command.Index == commandIndex)
					{
						areaCommand = command;
						break;
					}
				}
			}

			if (_securitySystemProtocol != null)
			{
				foreach (var areaIndex in areaIndexes)
				{
					foreach (var area in _securitySystemProtocol.Areas)
					{
						if (areaCommand != null && area.Index == areaIndex)
						{
							result = area.SendAreaCommand(commandIndex, password);
							break;
						}
					}
				}
			}

			return result;
		}


		#endregion

		#region Base Implementation

		#region JSON Converters

		protected override JsonConverter CreateDeviceSupportConverter()
		{
			return new DeviceSupportConverter();
		}

		#endregion JSON Converters

		public override void ConvertJsonFileToDriverData(string jsonString)
		{
			var obj = JsonConvert.DeserializeObject<BaseRootObject>(jsonString, CreateSerializerSettings());

			try
			{
				base.Initialize(obj);
			}
			catch (Exception ex)
			{
				Log(string.Format("SecuritySystem.SDK.SecuritySystemDriverIP.ConvertJsonFileToDriverData Error: {0}", ex.Message));
			}

		}

		public override CType AbstractClassType
		{
			get { return GetType(); }
		}


		public override void Dispose()
		{
			if (_securitySystemProtocol != null)
			{
				_securitySystemProtocol.StateChange -= OnProtocolStateChanged;
				_securitySystemProtocol.AlarmChange -= OnProtocolAlarmChanged;
				_securitySystemProtocol.ErrorChange -= OnProtocolErrorChanged;
				_securitySystemProtocol.RxOut -= SendRxOut;
				_securitySystemProtocol.ConnectedChanged -= OnProtocolConnectionChanged;
				_securitySystemProtocol.AreaListChanged -= OnProtocolAreaListChanged;
				_securitySystemProtocol.ZoneListChanged -= OnProtocolZoneListChanged;
				_securitySystemProtocol.SystemCommandResult -= OnProtocolCommandResultChanged;
			}

			base.Dispose();
		}

		#endregion

		#region Private Method

		private void SubscribeToProtocolEvents()
		{
			_securitySystemProtocol.StateChange += OnProtocolStateChanged;
			_securitySystemProtocol.ErrorChange += OnProtocolErrorChanged;
			_securitySystemProtocol.AlarmChange += OnProtocolAlarmChanged;
			_securitySystemProtocol.RxOut += SendRxOut;
			_securitySystemProtocol.ConnectedChanged += OnProtocolConnectionChanged;
			_securitySystemProtocol.AreaListChanged += OnProtocolAreaListChanged;
			_securitySystemProtocol.ZoneListChanged += OnProtocolZoneListChanged;
			_securitySystemProtocol.SystemCommandResult += OnProtocolCommandResultChanged;
		}

		private void InitializeKeypad()
		{
			var keypad = (_securitySystemKeypad as SecuritySystemKeypad);
			if (keypad != null)
			{
				keypad.Initialize(_securitySystemProtocol);
			}
		}

		private void RaiseSystemStateEvent(SecuritySystemState eventType, bool updatedState)
		{
			CrestronConsole.PrintLine("SecuritySystemDriverIP : RaiseSystemStateEvent is called for event Type - {0}", eventType);
			ListChangedEventArgs<SecuritySystemState> e = null;
			if (updatedState)
			{
				if (!_currentlyActiveStates.Contains(eventType))
				{
					int count = _currentlyActiveStates.Count;
					_currentlyActiveStates.Add(eventType);
					e = new ListChangedEventArgs<SecuritySystemState>(ListChangedAction.Added, SecuritySystemState.Unknown, eventType, count);
				}
			}
			else
			{
				if (_currentlyActiveStates.Contains(eventType))
				{
					int index = _currentlyActiveStates.IndexOf(eventType);
					if (index >= 0)
					{
						_currentlyActiveStates.Remove(eventType);
						e = new ListChangedEventArgs<SecuritySystemState>(ListChangedAction.Removed, eventType, SecuritySystemState.Unknown, index);
					}
				}
			}

			if (e != null)
			{
				var handler = SecuritysystemStateChangedEvent;
				if (handler != null)
				{
					handler(this, e);
					CrestronConsole.PrintLine("Driver : RaiseSystemStateEvent : Raised SecuritysystemStateChangedEvent event  ");
				}
				else
				{
					CrestronConsole.PrintLine("Driver : RaiseSystemStateEvent : SecuritysystemStateChangedEvent is null ");
				}
			}
		}

		private void OnProtocolStateChanged(object changedObject)
		{
			var newSecuritySystemState = (SecuritySystemStateArgs)changedObject;
			if (newSecuritySystemState != null)
			{
				RaiseSystemStateEvent(newSecuritySystemState.EventType, newSecuritySystemState.State);
			}
		}

		private void OnProtocolAlarmChanged(object changedObject)
		{
			CrestronConsole.PrintLine("SecuritySystemDriverIP : OnProtocolAlarmChanged is called");
			var obj = changedObject as SecuritySystemAlarmStateArgs;
			if (obj != null && obj.Alarm != null)
			{
				var alarmType = obj.Alarm.AlarmType;
				ListChangedEventArgs<SecuritySystemAlarmType> e = null;
				if (obj.Alarm.AlarmActive)
				{
					if (!_currentlyActiveAlarms.Contains(alarmType))
					{
						int count = _currentlyActiveAlarms.Count;
						_currentlyActiveAlarms.Add(alarmType);
						e = new ListChangedEventArgs<SecuritySystemAlarmType>(ListChangedAction.Added, SecuritySystemAlarmType.Unknown, alarmType, count);
					}
				}
				else
				{
					if (_currentlyActiveAlarms.Contains(alarmType))
					{
						int index = _currentlyActiveAlarms.IndexOf(alarmType);
						if (index >= 0)
						{
							_currentlyActiveAlarms.Remove(alarmType);
							e = new ListChangedEventArgs<SecuritySystemAlarmType>(ListChangedAction.Removed, alarmType, SecuritySystemAlarmType.Unknown, index);
						}
					}
				}

				if (e != null)
				{
					var handler = SecuritysystemAlarmStateChangedEvent;
					if (handler != null)
					{
						handler(this, e);
						CrestronConsole.PrintLine("SecuritySystemDriverIP : OnProtocolAlarmChanged : Raised SecuritysystemAlarmStateChangedEvent event ");
					}
					else
					{
						CrestronConsole.PrintLine("SecuritySystemDriverIP : OnProtocolAlarmChanged : SecuritysystemAlarmStateChangedEvent is null ");
					}
				}
			}
			else
			{
				CrestronConsole.PrintLine("SecuritySystemDriverIP : OnProtocolAlarmChanged : obj is null ");
			}
		}

		private void OnProtocolErrorChanged(object changedObject)
		{
			CrestronConsole.PrintLine("SecuritySystemDriverSerial : OnProtocolErrorChanged is called");
			var obj = changedObject as SecuritySystemErrorStateArgs;
			if (obj != null)
			{
				var error = obj.Error;
				ListChangedEventArgs<SecuritySystemError> e = null;
				if (obj.State)
				{
					if (!_currentlyActiveErrors.Contains(error))
					{
						int count = _currentlyActiveErrors.Count;
						_currentlyActiveErrors.Add(error);
						e = new ListChangedEventArgs<SecuritySystemError>(ListChangedAction.Added, SecuritySystemError.Unknown, error, count);
					}
				}
				else
				{
					if (_currentlyActiveErrors.Contains(error))
					{
						int index = _currentlyActiveErrors.IndexOf(error);
						if (index >= 0)
						{
							_currentlyActiveErrors.Remove(error);
							e = new ListChangedEventArgs<SecuritySystemError>(ListChangedAction.Removed, error, SecuritySystemError.Unknown, index);
						}
					}
				}

				if (e != null)
				{
					var handler = SecuritySystemErrorChanged; ;
					if (handler != null)
					{
						handler(this, e);
						CrestronConsole.PrintLine("Driver : OnProtocolErrorChanged : Raised SecuritySystemErrorChanged event ");
					}
					else
					{
						CrestronConsole.PrintLine("Driver : OnProtocolErrorChanged : SecuritySystemErrorChanged is null ");
					}
				}
			}
			else
			{
				CrestronConsole.PrintLine("Driver : OnProtocolErrorChanged : obj is null ");
			}
		}

		private void OnProtocolAreaListChanged(object sender, ListChangedEventArgs<ISecuritySystemArea> listChangedEventArgs)
		{
			var handler = SecuritySystemAreaListChanged;
			if (handler != null)
			{
				handler(sender, listChangedEventArgs);
				CrestronConsole.PrintLine("Driver : OnProtocolAreaListChanged : Raised SecuritySystemAreaListChanged event ");
			}
			else
			{
				CrestronConsole.PrintLine("Driver : OnProtocolAreaListChanged : SecuritySystemAreaListChanged is null ");
			}
		}

		private void OnProtocolConnectionChanged(object driver, ValueEventArgs<bool> e)
		{
			CrestronConsole.PrintLine("Driver :  OnProtocolConnectionChanged is called and value - " + (e != null ? e.Value : false));
			if (e != null &&
				e.Value != Connected)
			{
				Connected = e.Value;
				IsAuthenticated = true;
				Log(string.Format("ConnectedChanged - new state: {0}", e.Value));
			}
		}

		private void OnProtocolZoneListChanged(object sender, ListChangedEventArgs<ISecuritySystemZone> listChangedEventArgs)
		{
			var handler = SecuritySystemZoneListChanged;
			if (handler != null)
			{
				handler(sender, listChangedEventArgs);
				CrestronConsole.PrintLine("Driver : OnProtocolZoneListChanged : Raised SecuritySystemZoneListChanged event ");
			}
			else
			{
				CrestronConsole.PrintLine("Driver : OnProtocolZoneListChanged : SecuritySystemZoneListChanged is null ");
			}
		}

		private void OnProtocolCommandResultChanged(object sender, SecuritySystemCommandResultEventArgs args)
		{
			var handler = SecuritySystemCommandResult;
			if (handler != null)
			{
				handler(sender, args);
				CrestronConsole.PrintLine("Driver : OnProtocolCommandResultChanged : Raised SecuritySystemCommandResult event ");
			}
			else
			{
				CrestronConsole.PrintLine("Driver : OnProtocolCommandResultChanged : SecuritySystemCommandResult is null ");
			}
		}

		#endregion
	}
}
