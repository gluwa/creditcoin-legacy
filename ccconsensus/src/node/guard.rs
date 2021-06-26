#[derive(Clone, Copy, Debug, Hash, PartialEq, Eq, PartialOrd, Ord)]
#[repr(u8)]
pub enum Guard {
  Publish,
  Summarize,
  Finalize,
}
