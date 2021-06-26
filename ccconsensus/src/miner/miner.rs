use anyhow::Result;
use sawtooth_sdk::consensus::engine::Block;
use std::cell::RefCell;
use std::fmt::Debug;
use std::fmt::Formatter;
use std::fmt::Result as FmtResult;

use crate::block::BlockConsensus;
use crate::block::BlockHeader;
use crate::miner::Answer;
use crate::miner::Challenge;
use crate::miner::Worker;
use crate::node::PowConfig;
use crate::node::PowService;
use crate::utils::utc_seconds_f64;
use crate::work::get_difficulty;

pub struct Miner {
  worker: Worker,
  answer: RefCell<Option<Answer>>,
}

impl Miner {
  pub fn new() -> Result<Self> {
    Ok(Self {
      worker: Worker::new()?,
      answer: RefCell::new(None),
    })
  }

  pub fn try_create_consensus(&self) -> Result<Option<Vec<u8>>> {
    // Drain answers from the worker thread
    while let Some(answer) = self.worker.recv() {
      self.answer.borrow_mut().replace(answer);
    }

    if let Some(answer) = self.answer.borrow().as_ref() {
      let consensus: Vec<u8> = BlockConsensus::serialize(
        answer.challenge.difficulty,
        answer.challenge.timestamp,
        answer.nonce,
      );

      return Ok(Some(consensus));
    }

    Ok(None)
  }

  pub fn reset(&self) {
    self.clear_answer();
  }

  pub fn mine(
    &mut self,
    block_id: &[u8],
    peer_id: &[u8],
    service: &PowService,
    config: &PowConfig,
  ) -> Result<()> {
    let block: Block = service.get_block(block_id)?;
    let header: BlockHeader = BlockHeader::borrowed(&block)?;

    let timestamp: f64 = utc_seconds_f64();
    let difficulty: u32 = get_difficulty(&header, timestamp, service, config);

    let challenge: Challenge = Challenge {
      difficulty,
      timestamp,
      block_id: block_id.to_vec(),
      peer_id: peer_id.to_vec(),
    };

    self.worker.send(challenge);
    self.clear_answer();

    Ok(())
  }

  fn clear_answer(&self) {
    *self.answer.borrow_mut() = None;
  }
}

impl Debug for Miner {
  fn fmt(&self, f: &mut Formatter) -> FmtResult {
    f.debug_struct("Miner")
      .field("worker", &self.worker)
      .field("answer", &self.answer)
      .finish()
  }
}
