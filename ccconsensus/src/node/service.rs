use sawtooth_sdk::consensus::engine::Block;
use sawtooth_sdk::consensus::engine::BlockId;
use sawtooth_sdk::consensus::engine::Error;
use sawtooth_sdk::consensus::engine::PeerId;
use sawtooth_sdk::consensus::service::Service;
use std::cell::RefCell;
use std::collections::HashMap;
use std::fmt::Debug;
use std::fmt::Formatter;
use std::fmt::Result as FmtResult;
use std::rc::Rc;

#[repr(transparent)]
pub struct PowService {
  inner: Rc<RefCell<Box<dyn Service>>>,
}

impl PowService {
  pub fn new(service: Box<dyn Service>) -> Self {
    Self {
      inner: Rc::new(RefCell::new(service)),
    }
  }

  // ===========================================================================
  // Block Creation
  // ===========================================================================

  /// Initialize a new block built on the block with the given
  /// previous id and begin adding batches to it. If no previous
  /// id is specified, the current head will be used.
  pub fn initialize_block(&self, previous_id: Option<BlockId>) -> Result<(), Error> {
    self.inner.borrow_mut().initialize_block(previous_id)
  }

  /// Stop adding batches to the current block and return
  /// a summary of its contents.
  pub fn summarize_block(&self) -> Result<Vec<u8>, Error> {
    self.inner.borrow_mut().summarize_block()
  }

  /// Insert the given consensus data into the block and sign it.
  ///
  /// Note: If this call is successful, a BlockNew update
  ///       will be received with the new block afterwards.
  pub fn finalize_block(&self, data: Vec<u8>) -> Result<BlockId, Error> {
    self.inner.borrow_mut().finalize_block(data)
  }

  /// Stop adding batches to the current block and abandon it.
  pub fn cancel_block(&self) -> Result<(), Error> {
    match self.inner.borrow_mut().cancel_block() {
      Ok(()) => Ok(()),
      Err(Error::InvalidState(_)) => Ok(()),
      Err(error) => Err(error),
    }
  }

  // ===========================================================================
  // Block Management
  // ===========================================================================

  /// Update the prioritization of blocks to check.
  ///
  /// Note: The results of all checks will be sent
  ///       as BlockValid and BlockInvalid updates.
  pub fn check_blocks(&self, priority: Vec<BlockId>) -> Result<(), Error> {
    self.inner.borrow_mut().check_blocks(priority)
  }

  /// Update the block that should be committed.
  ///
  /// Note: This block must already have been checked.
  pub fn commit_block(&self, block_id: BlockId) -> Result<(), Error> {
    self.inner.borrow_mut().commit_block(block_id)
  }

  /// Signal that this block is no longer being committed.
  pub fn ignore_block(&self, block_id: BlockId) -> Result<(), Error> {
    self.inner.borrow_mut().ignore_block(block_id)
  }

  /// Mark this block as invalid from the perspective of consensus.
  ///
  /// Note: This will also fail all descendants.
  pub fn fail_block(&self, block_id: BlockId) -> Result<(), Error> {
    self.inner.borrow_mut().fail_block(block_id)
  }

  // ===========================================================================
  // P2P
  // ===========================================================================

  /// Send a consensus message to a specific, connected peer.
  #[allow(clippy::ptr_arg)]
  pub fn send_to(&self, peer: &PeerId, type_: &str, payload: Vec<u8>) -> Result<(), Error> {
    self.inner.borrow_mut().send_to(peer, type_, payload)
  }

  /// Broadcast a message to all connected peers.
  pub fn broadcast(&self, type_: &str, payload: Vec<u8>) -> Result<(), Error> {
    self.inner.borrow_mut().broadcast(type_, payload)
  }

  // ===========================================================================
  // Querying
  // ===========================================================================

  /// Retrieve consensus-related information about blocks
  pub fn get_blocks(&self, block_ids: Vec<BlockId>) -> Result<HashMap<BlockId, Block>, Error> {
    self.inner.borrow_mut().get_blocks(block_ids)
  }

  /// Retrieve consensus-related information about a specific block
  pub fn get_block(&self, block_id: &[u8]) -> anyhow::Result<Block> {
    self
      .get_blocks(vec![block_id.to_owned()])?
      .remove(block_id)
      .ok_or_else(|| anyhow!("Block not found: {}", dbg_hex!(block_id)))
  }

  /// Get the chain head block.
  pub fn get_chain_head(&self) -> Result<Block, Error> {
    self.inner.borrow_mut().get_chain_head()
  }

  /// Read the value of settings as of the given block
  pub fn get_settings(
    &self,
    block_id: BlockId,
    keys: Vec<String>,
  ) -> Result<HashMap<String, String>, Error> {
    self.inner.borrow_mut().get_settings(block_id, keys)
  }

  /// Read values in state as of the given block
  pub fn get_state(
    &self,
    block_id: BlockId,
    addresses: Vec<String>,
  ) -> Result<HashMap<String, Vec<u8>>, Error> {
    self.inner.borrow_mut().get_state(block_id, addresses)
  }
}

impl Debug for PowService {
  fn fmt(&self, f: &mut Formatter) -> FmtResult {
    write!(f, "(PowService)")
  }
}

impl Clone for PowService {
  fn clone(&self) -> Self {
    Self {
      inner: Rc::clone(&self.inner),
    }
  }
}
