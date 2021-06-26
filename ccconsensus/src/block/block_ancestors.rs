use std::borrow::Cow;

use crate::block::BlockHeader;
use crate::node::PowService;

pub struct BlockAncestors<'a> {
  block: Option<Cow<'a, [u8]>>,
  service: &'a PowService,
}

impl<'a> BlockAncestors<'a> {
  pub fn new(block: &'a [u8], service: &'a PowService) -> Self {
    Self {
      service,
      block: Some(Cow::Borrowed(block)),
    }
  }
}

impl<'a> Iterator for BlockAncestors<'a> {
  type Item = BlockHeader<'a>;

  fn next(&mut self) -> Option<Self::Item> {
    let result: Option<Self::Item> = self
      .block
      .take()
      .and_then(|block| self.service.get_block(&block).ok())
      .and_then(|block| BlockHeader::owned(block).ok());

    self.block = match result {
      Some(ref block) => Some(Cow::Owned(block.previous_id.to_owned())),
      None => None,
    };

    result
  }
}
