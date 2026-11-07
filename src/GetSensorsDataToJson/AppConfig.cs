using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GetSensorsDataToJson
{

    public class AppConfig
    {
        public int SensorsData_TotalTimeout_Seconds { get; private set; } = 15; // 15 seconds

        public int SensorsData_Refresh_Seconds { get; private set; } = 60; // 60 seconds

        public bool Log_SensorData_ToFile { get; private set; } = false; //false

        public int SensorsData_ResetMinMax_Seconds { get; private set; } = 600; // 10 minutes

        public AppConfig()
        {
            try
            {
                SensorsData_TotalTimeout_Seconds = int.Parse(ConfigurationManager.AppSettings["SensorsData_TotalTimeout_Seconds"].Trim());
            }
            catch
            {
                SensorsData_TotalTimeout_Seconds = 15;
            }

            try
            {
                SensorsData_Refresh_Seconds = int.Parse(ConfigurationManager.AppSettings["SensorsData_Refresh_Seconds"].Trim());
            }
            catch
            {
                SensorsData_Refresh_Seconds = 60;
            }

            try
            {
                Log_SensorData_ToFile = bool.Parse(ConfigurationManager.AppSettings["Log_SensorData_ToFile"].Trim());
            }
            catch
            {
                Log_SensorData_ToFile = false;
            }

            try
            {
                SensorsData_ResetMinMax_Seconds = int.Parse(ConfigurationManager.AppSettings["SensorsData_ResetMinMax_Seconds"].Trim());

            }
            catch
            {
                SensorsData_ResetMinMax_Seconds = 600;
            }
        }
    }
}
