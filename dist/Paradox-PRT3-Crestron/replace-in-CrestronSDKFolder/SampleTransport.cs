using Crestron.RAD.Common.Transports;

namespace SecuritySystem_Crestron_SampleDriverModel_IP
{
	/// <summary>
	/// תעבורה פסיבית: אינה פותחת סוקט ואינה מזרימה נתוני-דמה.
	/// החיבור האמיתי ל-PRT3 מנוהל ע"י Prt3Engine בתוך SecuritySystemProtocol,
	/// כדי שלא ייפתחו שני חיבורים לאותו ממיר.
	/// </summary>
	public class SampleTransport : SimplTransport
	{
		public override void Start()
		{
			base.Start();

			var handler = DataHandler;
			if (handler != null)
			{
				// מסמן לפרוטוקול שהאתחול הושלם.
				handler("InitializationComplete");
			}

			// אין SendTransportData — הנתונים האמיתיים מגיעים ממנוע ה-PRT3.
		}
	}
}
