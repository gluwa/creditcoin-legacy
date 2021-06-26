use crate::miner::Challenge;

#[derive(Clone, Debug)]
pub struct Answer {
  pub challenge: Challenge,
  pub nonce: u64,
}
