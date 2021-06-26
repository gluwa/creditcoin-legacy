use anyhow::Result;
use sawtooth_sdk::consensus::engine::Block;
use sawtooth_sdk::consensus::engine::BlockId;
use sawtooth_sdk::consensus::engine::Error;
use sawtooth_sdk::consensus::engine::StartupState;
use sawtooth_sdk::consensus::engine::Update;
use sawtooth_sdk::consensus::service::Service;
use std::borrow::Cow;

use crate::block::BlockAncestors;
use crate::block::BlockConsensus;
use crate::block::BlockHeader;
use crate::block::BlockPrinter as Printer;
use crate::miner::Miner;
use crate::node::Guard;
use crate::node::PowConfig;
use crate::node::PowService;
use crate::node::PowState;

const NULL_BLOCK_IDENTIFIER: [u8; 8] = [0; 8];

pub struct PowNode {
  pub config: PowConfig,
  pub service: PowService,
  state: PowState,
  miner: Miner,
}

impl PowNode {
  pub fn new(service: Box<dyn Service>) -> Self {
    Self::with_config(service, PowConfig::new())
  }

  pub fn with_config(service: Box<dyn Service>, config: PowConfig) -> Self {
    let service: PowService = PowService::new(service);
    let state: PowState = PowState::new();
    let miner: Miner = Miner::new().unwrap();

    Self {
      config,
      service,
      state,
      miner,
    }
  }

  pub fn initialize(&mut self, state: StartupState) -> Result<()> {
    if state.chain_head.block_num > 1 {
      debug!("Starting from non-genesis: {}", Printer(&state.chain_head));
    }

    // Store the public key of this validator, for signing blocks
    self.state.peer_id = state.local_peer_info.peer_id;

    // Store the chain head id for quick comparisons when required
    self.state.chain_head = state.chain_head.block_id;

    // Set initial on-chain configuration
    self.reload_configuration()?;

    // Start the inital PoW process with the current chain head
    self.miner.mine(
      &self.state.chain_head,
      &self.state.peer_id,
      &self.service,
      &self.config,
    )?;

    // Initialize a new block based on the current chain head
    self.service.initialize_block(None)?;

    Ok(())
  }

  /// Fetch and store on-chain settings as of the current head height
  pub fn reload_configuration(&mut self) -> Result<()> {
    self
      .config
      .load(&self.service, self.state.chain_head.to_owned())
      .map_err(Into::into)
  }

  pub fn try_publish(&mut self) -> Result<()> {
    // If we already published at this height, exit early.
    if self.state.guards.contains(&Guard::Publish) {
      return Ok(());
    }

    let consensus: Vec<u8> = match self.miner.try_create_consensus()? {
      Some(consensus) => consensus,
      None => return Ok(()),
    };

    // Try summarizing the blocks contents with a digest
    match self.service.summarize_block() {
      Ok(_digest) => {
        // Finalize the block with the current consensus
        match self.service.finalize_block(consensus) {
          Ok(block_id) => {
            debug!("Publishing block: {}", dbg_hex!(&block_id));

            // Set publishing guard
            self.state.guards.insert(Guard::Publish);

            // Clear log guards
            self.state.guards.remove(&Guard::Finalize);
            self.state.guards.remove(&Guard::Summarize);

            // Reset the miner state
            self.miner.reset();
          }
          Err(Error::BlockNotReady) => {
            if self.state.guards.insert(Guard::Finalize) {
              trace!("Cannot finalize block: not ready");
            }
          }
          Err(error) => {
            self.state.guards.remove(&Guard::Finalize);
            return Err(error.into());
          }
        }
      }
      Err(Error::BlockNotReady) => {
        if self.state.guards.insert(Guard::Summarize) {
          trace!("Cannot summarize block: not ready");
        }
      }
      Err(error) => {
        self.state.guards.remove(&Guard::Summarize);
        return Err(error.into());
      }
    }

    Ok(())
  }

  pub fn handle_update(&mut self, update: Update) -> Result<bool> {
    match update {
      Update::BlockNew(block) => {
        self.on_block_new(block)?;
      }
      Update::BlockValid(block_id) => {
        self.on_block_valid(block_id)?;
      }
      Update::BlockInvalid(block_id) => {
        self.on_block_invalid(block_id)?;
      }
      Update::BlockCommit(block_id) => {
        self.on_block_commit(block_id)?;
      }
      Update::Shutdown => {
        return Ok(false);
      }
      Update::PeerConnected(_) | Update::PeerDisconnected(_) | Update::PeerMessage(_, _) => {
        // ignore peer-related messages
      }
    }

    Ok(true)
  }

  /// Called when a new block is received and validated
  fn on_block_new(&mut self, block: Block) -> Result<()> {
    debug!("Checking block consensus: {}", Printer(&block));

    // This should never happen under normal circumstances
    if block.previous_id == NULL_BLOCK_IDENTIFIER {
      error!("Received Update::BlockNew for genesis block!");
      return Ok(());
    }

    let header: Result<()> = BlockHeader::borrowed(&block).and_then(|header| header.validate());

    // Ensure the block consensus is valid
    match header {
      Ok(()) => {
        debug!("Passed consensus check: {}", Printer(&block));

        // Tell the validator to validate the block
        self.service.check_blocks(vec![block.block_id])?;
      }
      Err(error) => {
        debug!("Failed consensus check: {} - {:?}", Printer(&block), error);
        self.service.fail_block(block.block_id)?;
      }
    }

    Ok(())
  }

  /// Called when a block check succeeds
  fn on_block_valid(&mut self, block_id: BlockId) -> Result<()> {
    let cur_head: Block = self.service.get_block(&self.state.chain_head)?;
    let new_head: Block = self.service.get_block(&block_id)?;

    debug!(
      "Choosing between chain heads -- current: {} -- new: {}",
      Printer(&cur_head),
      Printer(&new_head),
    );

    self.compare_forks(cur_head, new_head)?;

    Ok(())
  }

  /// Called when a block check fails
  fn on_block_invalid(&mut self, block_id: BlockId) -> Result<()> {
    // Mark the block as failed by consensus, let the validator know
    self.service.fail_block(block_id)?;

    Ok(())
  }

  /// Called when a block commit completes
  fn on_block_commit(&mut self, block_id: BlockId) -> Result<()> {
    debug!("Chain head updated to {}", dbg_hex!(&block_id));

    // Stop adding batches to the current block and abandon it.
    self.service.cancel_block()?;

    // Refresh on-chain configuration
    self.reload_configuration()?;

    // Update local chain head reference
    self.state.chain_head = block_id.to_owned();

    // Remove the publishing guard, allow publishing a new block when appropriate
    self.state.guards.remove(&Guard::Publish);

    // Start the PoW process for this block
    self
      .miner
      .mine(&block_id, &self.state.peer_id, &self.service, &self.config)?;

    // Initialize a new block based on the updated chain head
    self.service.initialize_block(Some(block_id))?;

    Ok(())
  }

  fn compare_forks(&mut self, cur_head: Block, new_head: Block) -> Result<()> {
    if !BlockConsensus::is_pow_consensus(&new_head.payload) {
      debug!("Ignoring new block (consensus) {}", Printer(&new_head));
      self.service.ignore_block(new_head.block_id)?;
      return Ok(());
    }

    if !BlockConsensus::is_pow_consensus(&cur_head.payload) {
      // this should be only possible if we switched consensus modes and haven't yet commited a block
      let mut fork_block: Cow<Block> = Cow::Borrowed(&new_head);

      loop {
        if fork_block.previous_id == cur_head.block_id {
          debug!("Committing new block (consensus) {}", Printer(&new_head));
          self.service.commit_block(new_head.block_id)?;
          break;
        } else if !BlockConsensus::is_pow_consensus(&fork_block.payload) {
          // also happens with genesis blocks
          debug!("Ignoring new block (consensus) {}", Printer(&new_head));
          self.service.ignore_block(new_head.block_id)?;
          break;
        }

        fork_block = Cow::Owned(self.service.get_block(&fork_block.previous_id)?);
      }
    } else if new_head.block_num == cur_head.block_num + 1
      && new_head.previous_id == cur_head.block_id
    {
      debug!("Committing new block (next) {}", Printer(&new_head));
      self.service.commit_block(new_head.block_id)?;
    } else {
      self.resolve_fork(cur_head, new_head)?;
    }

    Ok(())
  }

  fn resolve_fork(&self, cur_head: Block, new_head: Block) -> Result<()> {
    let cur_diff_size: u64 = cur_head.block_num.saturating_sub(new_head.block_num);
    let new_diff_size: u64 = new_head.block_num.saturating_sub(cur_head.block_num);

    // Fetch all blocks from the current chain AFTER the head of the new chain
    // Inverse of `new_chain_orphans`.
    let cur_chain_orphans: Vec<BlockHeader> =
      BlockAncestors::new(&cur_head.previous_id, &self.service)
        .take(cur_diff_size as usize)
        .take_while(|block| block.is_pow())
        .collect();

    // Fetch all blocks from the new chain AFTER the head of the current chain.
    // Inverse of `cur_chain_orphans`.
    let new_chain_orphans: Vec<BlockHeader> =
      BlockAncestors::new(&new_head.previous_id, &self.service)
        .take(new_diff_size as usize)
        .take_while(|block| block.is_pow())
        .collect();

    // Convert both chain heads to `BlockHeader`s. Propagate errors since
    // PoW validation should have been an earlier step.
    let cur_header: BlockHeader = BlockHeader::borrowed(&cur_head)?;
    let new_header: BlockHeader = BlockHeader::borrowed(&new_head)?;

    // Fetch the earliest block from both orphan chains; default to the current head
    let cur_fork_head: &BlockHeader = new_chain_orphans.last().unwrap_or_else(|| &cur_header);
    let new_fork_head: &BlockHeader = cur_chain_orphans.last().unwrap_or_else(|| &new_header);

    debug_assert_eq!(cur_fork_head.block_num, new_fork_head.block_num);

    let cur_ancestors = BlockAncestors::new(&cur_fork_head.block_id, &self.service);
    let new_ancestors = BlockAncestors::new(&new_fork_head.block_id, &self.service);

    // Construct a `ForkChain` to quickly traverse ancestors in pairs.
    // Traverse until:
    //   1. A common ancestor is found
    //   3. Either block is a genesis block
    //   2. Either block is NOT a PoW block
    let (cur_fork_blocks, new_fork_blocks): (Vec<_>, Vec<_>) = cur_ancestors
      .zip(new_ancestors)
      .take_while(|(block_a, block_b)| block_a.block_id != block_b.block_id)
      .take_while(|(block_a, block_b)| !block_a.is_genesis() && !block_b.is_genesis())
      .take_while(|(block_a, block_b)| block_a.is_pow() && block_b.is_pow())
      .unzip();

    // Chain the new orphan chain with any uncommon
    // ancestors; sum the total amount of work.
    let new_work: u64 = new_chain_orphans
      .iter()
      .chain(new_fork_blocks.iter())
      .fold(0, |total, block| total + block.work());

    // Chain the current orphan chain with any uncommon
    // ancestors; sum the total amount of work.
    let cur_work: u64 = cur_chain_orphans
      .iter()
      .chain(cur_fork_blocks.iter())
      .fold(0, |total, block| total + block.work());

    // Commit the new fork if it has greater work
    if new_work > cur_work {
      debug!(
        "Committing new fork (work {}/{}) {}",
        new_work,
        cur_work,
        Printer(&new_head),
      );

      self.service.commit_block(new_head.block_id)?;
    } else {
      debug!(
        "Ignoring new fork (work {}/{}) {}",
        new_work,
        cur_work,
        Printer(&new_head),
      );

      self.service.ignore_block(new_head.block_id)?;
    }

    Ok(())
  }
}
