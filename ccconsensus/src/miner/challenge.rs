use sawtooth_sdk::consensus::engine::BlockId;
use sawtooth_sdk::consensus::engine::PeerId;
use std::fmt::Debug;
use std::fmt::Formatter;
use std::fmt::Result;

#[derive(Clone)]
pub struct Challenge {
  pub difficulty: u32,
  pub timestamp: f64,
  pub block_id: BlockId,
  pub peer_id: PeerId,
}

impl Debug for Challenge {
  fn fmt(&self, f: &mut Formatter) -> Result {
    f.debug_struct("Challenge")
      .field("difficulty", &self.difficulty)
      .field("timestamp", &self.timestamp)
      .field("block_id", &dbg_hex!(&self.block_id))
      .field("peer_id", &dbg_hex!(&self.peer_id))
      .finish()
  }
}
