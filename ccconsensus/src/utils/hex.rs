use anyhow::Result;

pub fn to_hex(bytes: &[u8]) -> String {
  bytes.iter().map(|byte| format!("{:02x}", byte)).collect()
}

pub fn unhex(hexed: &str) -> Result<Vec<u8>> {
  if hexed.len() % 2 != 0 {
    return Err(anyhow!("Invalid Hex String"));
  }

  (0..hexed.len() / 2)
    .map(|index| u8::from_str_radix(&hexed[index * 2..index * 2 + 2], 16))
    .map(|result| result.map_err(|error| error.into()))
    .collect()
}
