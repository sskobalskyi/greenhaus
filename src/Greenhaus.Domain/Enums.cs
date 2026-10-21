namespace Greenhaus.Domain;

public enum Tier { Novice, Gardener, Druid }

public enum Metric { Temperature, AirHumidity, SoilMoisture, Ph, Light, Airflow }

public enum ControlMode { Off, Notify, Auto }

public enum ReadingState { Ok, Attention, Offline, Unknown }

public enum ActuatorKind { Pump, Heater, Lamp, Fan, Humidifier }

public enum AiDataAccess { None, Current, History }

public enum ViewMode { Simple, Zones }

public enum PotKind { Pot, Balcony, Bed }

public enum ActuatorState { Idle, Running, Blocked, Fault }

public sealed record Range(double Min, double Max);