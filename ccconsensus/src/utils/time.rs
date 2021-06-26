use chrono::DateTime;
use chrono::Utc;

const NANOS_PER_SEC: u32 = 1_000_000_000;

/// Returns the current time as UTC seconds
pub fn utc_seconds() -> u64 {
  Utc::now().timestamp() as u64
}

/// Some as `utc_seconds`, with sub-second precision
pub fn utc_seconds_f64() -> f64 {
  let datetime: DateTime<Utc> = Utc::now();
  let seconds: f64 = datetime.timestamp() as f64;
  let nanos: f64 = datetime.timestamp_subsec_nanos() as f64;

  seconds + (nanos / NANOS_PER_SEC as f64)
}
