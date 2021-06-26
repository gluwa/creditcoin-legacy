use sawtooth_sdk::consensus::engine::Block;
use std::fmt::Debug;
use std::fmt::Display;
use std::fmt::Formatter;
use std::fmt::Result as FmtResult;
use std::ops::Deref;

#[derive(Clone, Copy)]
#[repr(transparent)]
pub struct BlockPrinter<'a>(pub &'a Block);

impl<'a> From<&'a Block> for BlockPrinter<'a> {
  fn from(block: &'a Block) -> Self {
    Self(block)
  }
}

impl Deref for BlockPrinter<'_> {
  type Target = Block;

  fn deref(&self) -> &Self::Target {
    self.0
  }
}

impl Debug for BlockPrinter<'_> {
  fn fmt(&self, f: &mut Formatter) -> FmtResult {
    f.debug_struct("Block")
      .field("block_num", &self.0.block_num)
      .field("block_id", &dbg_hex!(&self.0.block_id))
      .field("previous_id", &dbg_hex!(&self.0.previous_id))
      .field("signer_id", &dbg_hex!(&self.0.signer_id))
      .finish()
  }
}

impl Display for BlockPrinter<'_> {
  fn fmt(&self, f: &mut Formatter) -> FmtResult {
    write!(
      f,
      "Block({}, {}, {})",
      self.block_num,
      dbg_hex!(&self.block_id),
      dbg_hex!(&self.previous_id),
    )
  }
}
