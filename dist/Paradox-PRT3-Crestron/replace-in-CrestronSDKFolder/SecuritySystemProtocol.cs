using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Events;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.SecuritySystem;
using Crestron.SimplSharp;
using Mila.Paradox.Prt3;   // מנוע ה-PRT3 + הפרוטוקול שלנו

namespace SecuritySystem_Crestron_SampleDriverModel_IP
{
	public class SecuritySystemProtocol : ABaseDriverProtocol, IDisposable
	{
		#region Fields

		private string sampleAttributeValue = string.Empty;
		private ISecuritySystem _securitySystem;
		private List<ISecuritySystemZone> _zones { get; set; }
		private List<ISecuritySystemArea> _areas { get; set; }
		private bool _disposed = false;

		// ==== שילוב Paradox PRT3 ====
		private Prt3Engine _engine;
		private readonly Dictionary<int, SecuritySystemZone> _zoneMap = new Dictionary<int, SecuritySystemZone>();
		private readonly Dictionary<int, SecuritySystemArea> _areaMap = new Dictionary<int, SecuritySystemArea>();

		// >>> ערוך כאן: אילו גלאים ומחיצות לנטר, וקוד המשתמש לדריכה/נטרול <<<
		private static readonly int[] MonitoredZones = { 1, 2, 3, 4, 5, 6, 7, 8 };
		private static readonly int[] MonitoredAreas = { 1 };
		private const string UserPin = "1234";

		#endregion

		#region Property

		public ReadOnlyCollection<ISecuritySystemArea> Areas { get; private set; }
		public ReadOnlyCollection<ISecuritySystemZone> Zones { get; private set; }
		private Dictionary<StandardCommandsEnum, string> CommandsDictionary { get; set; }

		#endregion

		#region Ctor

		public SecuritySystemProtocol(ISecuritySystem system, ISerialTransport transportDriver, byte id)
			: base(transportDriver, id)
		{
			_securitySystem = system;
			InitializeCommands();
			InitializeCollections();

			CrestronConsole.PrintLine("SecuritySystemProtocol ctor is called");
		}

		#endregion

		#region Protected Memeber

		/// <summary>
		/// It's not needed for sample security system.
		/// </summary>
		protected override void ChooseDeconstructMethod(ValidatedRxData validatedData)
		{
		}

		protected override void ConnectionChangedEvent(bool connection)
		{
			var handler = ConnectedChanged;
			if (handler != null)
			{
				handler(this, new ValueEventArgs<bool>(connection));
			}
		}

		protected override void ConnectionChanged(bool connection)
		{
			CrestronConsole.PrintLine("Protocol : ConnectionChanged is called and IsConnected -{0} and connection - {1}", IsConnected, connection);
			if (connection == IsConnected) return;
			base.ConnectionChanged(connection);
		}

		#endregion

		#region Events

		public event StateChangeHandler StateChange;
		public event AlarmChangeHandler AlarmChange;
		public event ErrorChangeHandler ErrorChange;
		public event EventHandler<ValueEventArgs<bool>> ConnectedChanged;
		public event EventHandler<ListChangedEventArgs<ISecuritySystemZone>> ZoneListChanged;
		public event EventHandler<ListChangedEventArgs<ISecuritySystemArea>> AreaListChanged;
		public event EventHandler<SecuritySystemCommandResultEventArgs> SystemCommandResult;
		// this is for sample driver purpose, given option to change arm/disarm from keypad also
		public event StateChangeHandler KeypadChange;
		// this is for sample driver purpose, given option to change alarm from keypad also
		public event AlarmChangeHandler KeypadAlarmChange;

		#endregion

		#region Public Method

		/// <summary>
		/// Invoked by the transport class when it receives data from the controlled device.
		/// </summary>
		public override void DataHandler(string rx)
		{
			switch (rx)
			{
				case "InitializationComplete": ConnectionChanged(true); break;
				case "IsOffline": ConnectionChanged(false); break;
			}
		}

		// ============================================================
		//  Paradox PRT3 — הפעלת המנוע, יצירת Zones/Areas, ותרגום אירועים
		// ============================================================

		/// <summary>מופעל מהדרייבר עם Host/Port. יוצר Zones/Areas ומתחיל polling.</summary>
		public void StartEngine(string host, int port)
		{
			foreach (var a in MonitoredAreas) CreateArea(a);
			var firstArea = MonitoredAreas.Length > 0 ? MonitoredAreas[0] : 1;
			foreach (var z in MonitoredZones) CreateZone(z, firstArea);

			var cfg = new Prt3EngineConfig
			{
				Zones = new List<int>(MonitoredZones),
				Areas = new List<int>(MonitoredAreas),
				PollIntervalMs = 400
			};
			_engine = new Prt3Engine(new ParadoxTcpTransport(host, port), cfg) { Logger = LogMessage };
			_engine.ZoneChanged += OnEngineZoneChanged;
			_engine.AreaChanged += OnEngineAreaChanged;
			_engine.ConnectionChanged += (s, connected) => ConnectionChanged(connected);
			_engine.Start();
			LogMessage(string.Format("Paradox PRT3 engine started -> {0}:{1}", host, port));
		}

		private void CreateArea(int index)
		{
			var states = new List<SecuritySystemState>
				{ SecuritySystemState.ArmedAway, SecuritySystemState.ArmedStay, SecuritySystemState.Disarmed };
			var alarms = new List<SecuritySystemAlarmType>
				{ SecuritySystemAlarmType.Burglary, SecuritySystemAlarmType.Fire, SecuritySystemAlarmType.Alarm };
			var cmds = new List<SecuritySystemAreaCommand>
			{
				new SecuritySystemAreaCommand(1, SecuritySystemCommandType.Disarm, true),
				new SecuritySystemAreaCommand(2, SecuritySystemCommandType.Away,   true),
				new SecuritySystemAreaCommand(3, SecuritySystemCommandType.Stay,   true)
			};
			var area = new SecuritySystemArea("Area " + index, index, states.AsReadOnly(),
				alarms.AsReadOnly(), cmds.AsReadOnly(), this);
			area.SecuritysystemAreaStateChangedEvent += OnSecuritysystemAreaStateChangedEvent;
			area.SecuritysystemAlarmStateChangedEvent += OnSecuritysystemAlarmStateChangedEvent;
			_areas.Add(area);
			_areaMap[index] = area;
			var h = AreaListChanged;
			if (h != null) h(this, new ListChangedEventArgs<ISecuritySystemArea>(ListChangedAction.Added, null, area, _areas.Count - 1));
		}

		private void CreateZone(int index, int areaIndex)
		{
			var zone = new SecuritySystemZone("Zone " + index, index, areaIndex);
			_zones.Add(zone);
			_zoneMap[index] = zone;
			var h = ZoneListChanged;
			if (h != null) h(this, new ListChangedEventArgs<ISecuritySystemZone>(ListChangedAction.Added, null, zone, _zones.Count - 1));
		}

		private void OnEngineZoneChanged(object sender, ZoneChangedEventArgs e)
		{
			SecuritySystemZone zone;
			if (_zoneMap.TryGetValue(e.Zone, out zone))
				zone.SetActiveState(SecuritySystemZoneState.Faulted, e.IsMotion); // תנועה/פתוח = Faulted
		}

		private void OnEngineAreaChanged(object sender, AreaChangedEventArgs e)
		{
			SecuritySystemArea area;
			if (!_areaMap.TryGetValue(e.Area, out area)) return;
			if (e.Arm == ArmState.Armed) area.SetArmedMode(SecuritySystemCommandType.Away);
			else if (e.Arm == ArmState.Disarmed) area.SetArmedMode(SecuritySystemCommandType.Disarm);
			area.SetAlarmExternal(SecuritySystemAlarmType.Burglary, e.Alarm == AlarmState.InAlarm);
		}

		/// <summary>
		/// When user sets the user attribute in crestron home, this method will be called
		/// </summary>
		public override void SetUserAttribute(string attributeId, string attributeValue)
		{
			CrestronConsole.PrintLine("Set user attribute is called for  value  === > " + attributeValue);
			this.sampleAttributeValue = attributeValue;
		}

		public override void Dispose()
		{
			if (_engine != null) _engine.Stop();

			if (!_disposed)
			{
				if (_areas != null)
				{
					foreach (var area in _areas)
					{
						var sArea = (area as SecuritySystemArea);
						if (sArea == null) continue;

						sArea.SecuritysystemAreaStateChangedEvent -= OnSecuritysystemAreaStateChangedEvent;
						sArea.SecuritysystemAlarmStateChangedEvent -= OnSecuritysystemAlarmStateChangedEvent;
						sArea.Dispose();
					}

					_areas.Clear();
				}

				if (_zones != null)
				{
					_zones.Clear();
				}

				_disposed = true;
			}

			base.Dispose();
		}

		public void PrintAttributeValue()
		{
			CrestronConsole.PrintLine("Sample attribute value  === > " + this.sampleAttributeValue);
		}

		public SecuritySystemOperationalResult ExecuteSecurityCommands(int commandIndex, List<int> areaIndexes, string password)
		{
			SecuritySystemAreaCommand cmd = null;
			foreach (var c in _securitySystem.GetAvailableAreaCommands())
				if (c.Index == commandIndex) cmd = c;

			var code = SecuritySystemOperationalResultCode.Success;
			if (cmd == null)
				code = SecuritySystemOperationalResultCode.InvalidIdParameters;
			else if (_engine != null)
			{
				var pin = string.IsNullOrEmpty(password) ? UserPin : password;
				foreach (var areaIndex in areaIndexes)
				{
					switch (cmd.CommandType)
					{
						case SecuritySystemCommandType.Disarm: _engine.Disarm(areaIndex, pin); break;
						case SecuritySystemCommandType.Away:
						case SecuritySystemCommandType.ForceAway: _engine.Arm(areaIndex, pin, ArmMode.Regular); break;
						case SecuritySystemCommandType.Stay:
						case SecuritySystemCommandType.ForceStay: _engine.Arm(areaIndex, pin, ArmMode.Stay); break;
					}
				}
			}

			var result = new SecuritySystemOperationalResult(1)
			{
				CommandType = cmd != null ? cmd.CommandType : SecuritySystemCommandType.Disarm,
				TargetComponentId = areaIndexes,
				Result = code
			};
			var handler = SystemCommandResult;
			if (handler != null) handler(this, new SecuritySystemCommandResultEventArgs() { CommandResult = result });
			return result;   // מצב הדריכה בפועל יתעדכן כשה-polling של RA יזהה את השינוי
		}

		#endregion

		#region Logging

		internal void LogMessage(string message)
		{
			if (!EnableLogging) return;

			if (CustomLogger == null)
			{
				CrestronConsole.PrintLine(message);
			}
			else
			{
				CustomLogger(message + "\n");
			}
		}

		#endregion Logging

		#region Private Method

		private void UpdateStateError(SecuritySystemError errorType, bool updatedState)
		{
			var stateObj = new SecuritySystemErrorStateArgs
			{
				Error = errorType,
				State = updatedState
			};
			RaiseEventError(stateObj);
		}

		private void RaiseEventError(object obj)
		{
			var handler = ErrorChange;
			if (handler != null)
			{
				CrestronConsole.PrintLine(" Security system protocol: raised ErrorChange event");
				handler(obj);
			}
			else
			{
				CrestronConsole.PrintLine(" Security system protocol:  ErrorChange is null");
			}
		}

		private void RaiseAreaStateChangedEvent(SecuritySystemState eventType, bool state)
		{
			var stateObj = new SecuritySystemStateArgs
			{
				EventType = eventType,
				State = state
			};

			var handler = StateChange;
			if (handler != null)
			{
				handler(stateObj);
			}
		}

		private void RaiseKeypadStateChangedEvent(SecuritySystemState eventType, bool state)
		{
			var stateObj = new SecuritySystemStateArgs
			{
				EventType = eventType,
				State = state
			};

			var handler = KeypadChange;
			if (handler != null)
			{
				handler(stateObj);
			}
		}

		private void RaiseKeypadAlarmChangedEvent(SecuritySystemAlarmType eventType, bool state)
		{
			var stateObj = new SecuritySystemAlarmStateArgs
			{
				Alarm = new SecuritySystemAlarm(eventType, state),
				State = state
			};

			var handler = KeypadAlarmChange;
			if (handler != null)
			{
				handler(stateObj);
			}
		}

		private void RaiseAlarmStateChangedEvent(SecuritySystemAlarmType eventType, bool state)
		{
			var stateObj = new SecuritySystemAlarmStateArgs
			{
				Alarm = new SecuritySystemAlarm(eventType, state),
				State = state
			};

			var handler = AlarmChange;
			if (handler != null)
			{
				handler(stateObj);
			}
		}

		private void InitializeCollections()
		{
			_areas = new List<ISecuritySystemArea>();
			_zones = new List<ISecuritySystemZone>();

			if (Zones == null)
				Zones = new ReadOnlyCollection<ISecuritySystemZone>(_zones);

			if (Areas == null)
				Areas = new ReadOnlyCollection<ISecuritySystemArea>(_areas);
		}

		private void InitializeCommands()
		{
			CommandsDictionary = new Dictionary<StandardCommandsEnum, string>()
            {
                {StandardCommandsEnum._0, "\x00"},
                {StandardCommandsEnum._1, "\x01"},
                {StandardCommandsEnum._2, "\x02"},
                {StandardCommandsEnum._3, "\x03"},
                {StandardCommandsEnum._4, "\x04"},
                {StandardCommandsEnum._5, "\x05"},
                {StandardCommandsEnum._6, "\x06"},
                {StandardCommandsEnum._7, "\x07"},
                {StandardCommandsEnum._8, "\x08"},
                {StandardCommandsEnum._9, "\x09"},
                {StandardCommandsEnum.Asterisk, "\x0A"},
                {StandardCommandsEnum.Period, "Period"},
                {StandardCommandsEnum.Dash, "Dash"},
                {StandardCommandsEnum.Octothorpe, "\x0B"},
                {StandardCommandsEnum.FunctionButton1, "\x1C"},
                {StandardCommandsEnum.FunctionButton2, "\x1D"},
                {StandardCommandsEnum.FunctionButton3, "\x1E"},
                {StandardCommandsEnum.FunctionButton4, "\x1F"},
                {StandardCommandsEnum.Menu, "Menu"},
                {StandardCommandsEnum.Home, "Home"},
                {StandardCommandsEnum.Exit, "Exit"},
                {StandardCommandsEnum.Clear, "Clear"},
                {StandardCommandsEnum.RightArrow, "RightArrow"},
                {StandardCommandsEnum.LeftArrow, "LeftArrow"},
                {StandardCommandsEnum.DownArrow, "DownArrow"},
                {StandardCommandsEnum.UpArrow, "UpArrow"},
                {StandardCommandsEnum.KeypadBackSpace, "KeypadBackSpace"},
                { StandardCommandsEnum.SetSystemStateToArmStay,"SetSystemStateToArmStay"},
                { StandardCommandsEnum.SetSystemStateToDisarmed,"SetSystemStateToDisarmed"},
                { StandardCommandsEnum.SetSystemStateToArmInstant,"SetSystemStateToArmInstant"},
            };
		}

		private void OnSecuritysystemAreaStateChangedEvent(object sender, ListChangedEventArgs<SecuritySystemState> args)
		{
			var updatedState = false;
			if (args != null)
			{
				var state = SecuritySystemState.Unknown;
				switch (args.ChangedAction)
				{
					case ListChangedAction.Added:
						state = args.NewItem;
						updatedState = true;
						break;
					case ListChangedAction.Removed:
						state = args.OldItem;
						break;
				}

				RaiseAreaStateChangedEvent(state, updatedState);
			}
		}

		private void OnSecuritysystemAlarmStateChangedEvent(object sender, ListChangedEventArgs<SecuritySystemAlarmType> args)
		{
			if (args != null)
			{
				var type = SecuritySystemAlarmType.Unknown;
				var state = false;
				switch (args.ChangedAction)
				{
					case ListChangedAction.Added:
						type = args.NewItem;
						state = true;
						break;
					case ListChangedAction.Removed:
						type = args.OldItem;
						break;
				}

				RaiseAlarmStateChangedEvent(type, state);
			}
		}

		#endregion

		#region Keypad Operation

		public void SendKeypadNumber(uint num)
		{
			if (num == 0 && CommandsDictionary.ContainsKey(StandardCommandsEnum._0))
			{
				var commandSet = new CommandSet("0", "0", CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum._0);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("KeypadNumber method is called for number " + num);

			while (num > 0)
			{
				var digit = num % 10;
				StandardCommandsEnum numberEnum;
				switch (digit)
				{
					case 0:
						numberEnum = StandardCommandsEnum._0;
						break;
					case 1:
						numberEnum = StandardCommandsEnum._1;
						RaiseAreaStateChangedEvent(SecuritySystemState.ArmedStay, true);
						RaiseKeypadStateChangedEvent(SecuritySystemState.ArmedStay, true);
						break;
					case 2:
						numberEnum = StandardCommandsEnum._2;
						RaiseAreaStateChangedEvent(SecuritySystemState.Disarmed, true);
						RaiseKeypadStateChangedEvent(SecuritySystemState.Disarmed, true);
						break;
					case 3:
						numberEnum = StandardCommandsEnum._3;
						PrintAttributeValue();
						break;
					case 4:
						numberEnum = StandardCommandsEnum._4;
						break;
					case 5:
						numberEnum = StandardCommandsEnum._5;
						break;
					case 6:
						numberEnum = StandardCommandsEnum._6;
						break;
					case 7:
						numberEnum = StandardCommandsEnum._7;
						break;
					case 8:
						numberEnum = StandardCommandsEnum._8;
						break;
					case 9:
						numberEnum = StandardCommandsEnum._9;
						break;
					default:
						return;
				}

				if (!CommandsDictionary.ContainsKey(numberEnum)) return;

				var commandSet = new CommandSet(numberEnum.ToString(), numberEnum.ToString(), CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, numberEnum);
				PrepareStringThenSend(commandSet);
				num = num / 10;
			}
		}

		public void SendKeypadPound()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Octothorpe))
			{
				var commandSet = new CommandSet("KeypadPound", StandardCommandsEnum.Octothorpe.ToString(), CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.KeypadPound);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Pound method is called");
		}

		public void SendKeypadAsterisk()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Asterisk))
			{
				var commandSet = new CommandSet("KeypadAsterisk", StandardCommandsEnum.Asterisk.ToString(), CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Asterisk);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Asterisk method is called");
		}

		public void SendKeypadPeriod()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Period))
			{
				var commandSet = new CommandSet("Period", StandardCommandsEnum.Period.ToString(), CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Period);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Period method is called");
		}

		public void SendKeypadDash()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Dash))
			{
				var commandSet = new CommandSet("Dash", StandardCommandsEnum.Dash.ToString(), CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Dash);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Dash method is called");
		}

		public void SendKeypadString(string keys)
		{
			if (keys.Length >= 20)
				keys = keys.Substring(0, 20);

			foreach (var key in keys)
			{
				var commandName = string.Empty;
				StandardCommandsEnum commandEnums = StandardCommandsEnum._0;

				switch (key)
				{
					case '0':
						commandName = "0";
						break;
					case '1':
						commandEnums = StandardCommandsEnum._1;
						commandName = "1";
						break;
					case '2':
						commandEnums = StandardCommandsEnum._2;
						commandName = "2";
						break;
					case '3':
						commandEnums = StandardCommandsEnum._3;
						commandName = "3";
						break;
					case '4':
						commandEnums = StandardCommandsEnum._4;
						commandName = "4";
						break;
					case '5':
						commandEnums = StandardCommandsEnum._5;
						commandName = "5";
						break;
					case '6':
						commandEnums = StandardCommandsEnum._6;
						commandName = "6";
						break;
					case '7':
						commandEnums = StandardCommandsEnum._7;
						commandName = "7";
						break;
					case '8':
						commandEnums = StandardCommandsEnum._8;
						commandName = "8";
						break;
					case '9':
						commandEnums = StandardCommandsEnum._9;
						commandName = "9";
						break;
					case '.':
						commandEnums = StandardCommandsEnum.Period;
						commandName = ".";
						break;
					case '-':
						commandEnums = StandardCommandsEnum.Dash;
						commandName = "-";
						break;
					case '*':
						commandEnums = StandardCommandsEnum.Asterisk;
						commandName = "*";
						break;
					case '#':
						commandEnums = StandardCommandsEnum.Octothorpe;
						commandName = "#";
						break;
					default:
						continue;
				}

				var commandSet = new CommandSet(commandName, commandEnums.ToString(),
					CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, commandEnums);
				PrepareStringThenSend(commandSet);

				LogMessage("SendKeypadString method is called for commandName : " + commandName + ", enum : " + commandEnums);
			}
		}

		public void SendKeypadBackSpace()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.KeypadBackSpace))
			{
				var commandSet = new CommandSet("KeypadBackSpace", StandardCommandsEnum.KeypadBackSpace.ToString(), CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.KeypadBackSpace);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("KeypadBackSpace method is called ");
		}

		public void SendKeypadArrowKeys(ArrowDirections direction)
		{
			StandardCommandsEnum numberEnum;
			switch (direction)
			{
				case ArrowDirections.Up:
					numberEnum = StandardCommandsEnum.UpArrow;
					break;
				case ArrowDirections.Down:
					numberEnum = StandardCommandsEnum.DownArrow;
					break;
				case ArrowDirections.Left:
					numberEnum = StandardCommandsEnum.LeftArrow;
					break;
				case ArrowDirections.Right:
					numberEnum = StandardCommandsEnum.RightArrow;
					break;
				default:
					return;
			}

			if (!CommandsDictionary.ContainsKey(numberEnum)) return;

			var commandSet = new CommandSet(numberEnum.ToString(), numberEnum.ToString(),
				  CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, numberEnum);
			PrepareStringThenSend(commandSet);

			LogMessage("ArrowKey method is called for direction  " + numberEnum);
		}

		public void SendKeypadEnter()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Enter))
			{
				var commandSet = new CommandSet("Enter", "Enter",
				CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Enter);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Enter method is called");
		}

		public void SendKeypadClear()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Clear))
			{
				var commandSet = new CommandSet("Clear", "Clear",
							  CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Clear);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Clear method is called");
		}

		public void SendKeypadExit()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Exit))
			{
				var commandSet = new CommandSet("Exit", "Exit",
								CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Exit);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Exit method is called");
		}

		public void SendKeypadHome()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Home))
			{
				var commandSet = new CommandSet("Home", "Home",
							 CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Home);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Home method is called");
		}

		public void SendKeypadMenu()
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.Menu))
			{
				var commandSet = new CommandSet("Menu", "Menu",
							   CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, StandardCommandsEnum.Menu);
				PrepareStringThenSend(commandSet);
			}

			LogMessage("Menu method is called");
		}

		/// Trigger the security system keypad function button
		public void TriggerFunctionButton(int buttonNumber)
		{
			if (CommandsDictionary.ContainsKey(StandardCommandsEnum.FunctionButton1))
			{
				StandardCommandsEnum commandEnum = StandardCommandsEnum.FunctionButton1;
				switch (buttonNumber)
				{
					case 1:
						commandEnum = StandardCommandsEnum.FunctionButton1;
						RaiseAreaStateChangedEvent(SecuritySystemState.ArmedStay, true);
						RaiseKeypadStateChangedEvent(SecuritySystemState.ArmedStay, true);
						break;
					case 2:
						commandEnum = StandardCommandsEnum.FunctionButton2;
						RaiseAreaStateChangedEvent(SecuritySystemState.ArmedAway, true);
						RaiseKeypadStateChangedEvent(SecuritySystemState.ArmedAway, true);
						break;
					case 3:
						commandEnum = StandardCommandsEnum.FunctionButton3;
						RaiseAlarmStateChangedEvent(SecuritySystemAlarmType.Fire, false);
						RaiseAlarmStateChangedEvent(SecuritySystemAlarmType.Fire, true);

						RaiseKeypadAlarmChangedEvent(SecuritySystemAlarmType.Fire, false);
						RaiseKeypadAlarmChangedEvent(SecuritySystemAlarmType.Fire, true);

						break;
					case 4:
						commandEnum = StandardCommandsEnum.FunctionButton4;
						break;
					case 5:
						commandEnum = StandardCommandsEnum.FunctionButton5;
						break;
					case 6:
						commandEnum = StandardCommandsEnum.FunctionButton6;
						break;
					case 7:
						commandEnum = StandardCommandsEnum.FunctionButton7;
						break;
					case 8:
						commandEnum = StandardCommandsEnum.FunctionButton8;
						break;
					default:
						return;
				}

				var commandSet = new CommandSet(commandEnum.ToString(), commandEnum.ToString(),
						   CommonCommandGroupType.Unknown, null, false, CommandPriority.Low, commandEnum);
				PrepareStringThenSend(commandSet);

				LogMessage("TriggerFunctionButton method is called for  " + commandEnum);
			}
		}

		#endregion
	}

	public delegate void StateChangeHandler(object changedObject);
	public delegate void AlarmChangeHandler(object changedObject);
	public delegate void ErrorChangeHandler(object changedObject);

	internal class SecuritySystemStateArgs
	{
		public SecuritySystemState EventType { get; set; }
		public bool State { get; set; }
	}

	internal class SecuritySystemErrorStateArgs
	{
		public SecuritySystemError Error { get; set; }
		public bool State { get; set; }
	}

	internal class SecuritySystemAlarmStateArgs
	{
		public SecuritySystemAlarm Alarm { get; set; }
		public bool State { get; set; }
	}
}
