use anyhow::Result;
use sawtooth_sdk::consensus::engine::Block;
use sawtooth_sdk::consensus::engine::BlockId;
use sha2::Digest;
use sha2::Sha256;
use std::borrow::Cow;

use crate::block::BlockConsensus;
use crate::block::BlockHeader;
use crate::node::PowConfig;
use crate::node::PowService;
use crate::primitives::H256;

pub type Hasher = Sha256;

pub fn get_hasher() -> Hasher {
  Sha256::new()
}

pub fn mkhash(hasher: &mut Hasher, block_id: &[u8], peer_id: &[u8], nonce: u64) -> H256 {
  let mut output: H256 = H256::new();

  mkhash_into(hasher, &mut output, block_id, peer_id, nonce);

  output
}

pub fn mkhash_into(
  hasher: &mut Hasher,
  output: &mut H256,
  block_id: &[u8],
  peer_id: &[u8],
  nonce: u64,
) {
  hasher.update(block_id);
  hasher.update(peer_id);
  hasher.update(nonce.to_string().as_bytes());
  output.copy_from_slice(&*hasher.finalize_reset());
}

pub fn is_valid_proof_of_work(hash: &H256, difficulty: u32) -> bool {
  digest_score(hash) >= difficulty
}

pub fn get_difficulty(
  header: &BlockHeader,
  timestamp: f64,
  service: &PowService,
  config: &PowConfig,
) -> u32 {
  if header.is_genesis() {
    return config.initial_difficulty;
  }

  calculate_difficulty(header, timestamp, service, config).unwrap_or(config.initial_difficulty)
}

fn calculate_difficulty(
  header: &BlockHeader,
  timestamp: f64,
  service: &PowService,
  config: &PowConfig,
) -> Result<u32> {
  if is_tuning_block(header, config) {
    if let Some(difficulty) = calculate_tuning_difficulty(header, timestamp, service, config)? {
      return Ok(difficulty);
    }
  } else if is_adjustment_block(header, config) {
    if let Some(difficulty) = calculate_adjustment_difficulty(header, timestamp, service, config)? {
      return Ok(difficulty);
    }
  }

  Ok(header.consensus.difficulty)
}

fn calculate_tuning_difficulty(
  header: &BlockHeader,
  timestamp: f64,
  service: &PowService,
  config: &PowConfig,
) -> Result<Option<u32>> {
  let (time_taken, time_expected) = elapsed_time(
    header,
    service,
    timestamp,
    config.difficulty_tuning_block_count,
    config.seconds_between_blocks,
  )?;

  let difficulty: u32 = header.consensus.difficulty;

  if time_taken < time_expected && difficulty < 255 {
    Ok(Some(difficulty + 1))
  } else if time_taken > time_expected && difficulty > 0 {
    Ok(Some(difficulty - 1))
  } else {
    Ok(None)
  }
}

fn calculate_adjustment_difficulty(
  header: &BlockHeader,
  timestamp: f64,
  service: &PowService,
  config: &PowConfig,
) -> Result<Option<u32>> {
  let (time_taken, time_expected) = elapsed_time(
    header,
    service,
    timestamp,
    config.difficulty_adjustment_block_count,
    config.seconds_between_blocks,
  )?;

  let difficulty: u32 = header.consensus.difficulty;

  if time_taken < time_expected / 2.0 && difficulty < 255 {
    Ok(Some(difficulty + 1))
  } else if time_taken > time_expected * 2.0 && difficulty > 0 {
    Ok(Some(difficulty - 1))
  } else {
    Ok(None)
  }
}

fn is_tuning_block(header: &BlockHeader, config: &PowConfig) -> bool {
  header.block_num % config.difficulty_tuning_block_count == 0
}

fn is_adjustment_block(header: &BlockHeader, config: &PowConfig) -> bool {
  header.block_num % config.difficulty_adjustment_block_count == 0
}

fn elapsed_time(
  header: &BlockHeader,
  service: &PowService,
  current_time: f64,
  total_count: u64,
  expected_interval: u64,
) -> Result<(f64, f64)> {
  let mut count: u64 = 2;
  let mut previous_time: f64 = header.consensus.timestamp;
  let mut block_id: Cow<BlockId> = Cow::Borrowed(&header.previous_id);

  loop {
    let block: Block = service.get_block(&block_id)?;

    if !BlockConsensus::is_pow_consensus(&block.payload) {
      break;
    }

    let timestamp: f64 = match BlockConsensus::deserialize(&block.payload) {
      Ok(consensus) => consensus.timestamp,
      Err(error) => panic!("Failed to parse PoW consensus: {}", error),
    };

    count += 1;
    block_id = Cow::Owned(block.previous_id);
    previous_time = timestamp;

    if count >= total_count {
      break;
    }
  }

  let time_taken: f64 = current_time - previous_time;
  let time_expected: f64 = (count * expected_interval) as f64;

  Ok((time_taken, time_expected))
}

pub fn digest_score(digest: &H256) -> u32 {
  let mut score: u32 = 0;

  for byte in digest.iter().copied() {
    if byte > 0 {
      if byte >= 128 {
        continue;
      } else if byte >= 64 {
        score += 1;
      } else if byte >= 32 {
        score += 2;
      } else if byte >= 16 {
        score += 3;
      } else if byte >= 8 {
        score += 4;
      } else if byte >= 4 {
        score += 5;
      } else if byte >= 2 {
        score += 6;
      } else {
        score += 7;
      }
      break;
    } else {
      score += 8;
    }
  }

  score
}
