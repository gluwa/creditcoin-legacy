macro_rules! conf_key {
  ($name:expr) => {
    concat!("sawtooth.consensus.pow.", $name)
  };
}

macro_rules! dbg_hex {
  ($expr:expr) => {
    &$crate::utils::to_hex($expr)[..16]
  };
}
