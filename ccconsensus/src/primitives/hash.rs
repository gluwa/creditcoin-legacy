macro_rules! impl_hash {
  ($name:ident, $size:expr) => {
    #[derive(Clone, Copy)]
    #[repr(transparent)]
    pub struct $name {
      inner: [u8; $size],
    }

    impl $name {
      pub const SIZE: usize = $size;

      pub const MIN: Self = Self {
        inner: [u8::min_value(); $size],
      };

      pub const MAX: Self = Self {
        inner: [u8::max_value(); $size],
      };

      pub const fn new() -> Self {
        Self { inner: [0; $size] }
      }

      pub fn random() -> Self {
        let mut this: Self = Self::new();
        ::rand::Rng::fill(&mut ::rand::thread_rng(), &mut this[..]);
        this
      }

      pub fn reversed(&self) -> Self {
        let mut this: Self = *self;
        this.reverse();
        this
      }

      pub fn to_hex(&self) -> String {
        $crate::utils::to_hex(self)
      }
    }

    impl ::std::ops::Deref for $name {
      type Target = [u8];

      fn deref(&self) -> &Self::Target {
        &self.inner
      }
    }

    impl ::std::ops::DerefMut for $name {
      fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.inner
      }
    }

    impl AsRef<[u8]> for $name {
      fn as_ref(&self) -> &[u8] {
        &self.inner
      }
    }

    impl From<[u8; $size]> for $name {
      fn from(inner: [u8; $size]) -> Self {
        $name { inner }
      }
    }

    impl From<$name> for [u8; $size] {
      fn from(this: $name) -> Self {
        this.inner
      }
    }

    impl ::std::fmt::Debug for $name {
      fn fmt(&self, f: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
        write!(f, "{}", self.to_hex())
      }
    }

    impl ::std::fmt::Display for $name {
      fn fmt(&self, f: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
        write!(f, "{}", self.to_hex())
      }
    }

    impl Default for $name {
      fn default() -> Self {
        Self::new()
      }
    }

    impl PartialEq for $name {
      fn eq(&self, other: &Self) -> bool {
        (&**self).eq(&**other)
      }
    }

    impl Eq for $name {}

    impl PartialOrd for $name {
      fn partial_cmp(&self, other: &Self) -> Option<::std::cmp::Ordering> {
        (&**self).partial_cmp(&**other)
      }
    }

    impl Ord for $name {
      fn cmp(&self, other: &Self) -> ::std::cmp::Ordering {
        (&**self).cmp(&**other)
      }
    }

    impl ::std::hash::Hash for $name {
      fn hash<H: ::std::hash::Hasher>(&self, hasher: &mut H) {
        self.inner.hash(hasher)
      }
    }
  };
}

impl_hash!(H256, 32);
