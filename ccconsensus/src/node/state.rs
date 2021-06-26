use sawtooth_sdk::consensus::engine::BlockId;
use sawtooth_sdk::consensus::engine::PeerId;
use std::collections::BTreeSet;

use crate::node::Guard;

#[derive(Debug, Default)]
pub struct PowState {
  pub chain_head: BlockId,
  pub peer_id: PeerId,
  pub guards: BTreeSet<Guard>,
}

impl PowState {
  pub fn new() -> Self {
    Self::default()
  }
}
