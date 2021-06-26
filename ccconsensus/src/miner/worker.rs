use anyhow::Result;
use rand::rngs::ThreadRng;
use rand::thread_rng;
use rand::Rng;
use std::thread::Builder;
use std::thread::JoinHandle;

use crate::miner::Answer;
use crate::miner::Challenge;
use crate::miner::Channel;
use crate::primitives::H256;
use crate::utils::to_hex;
use crate::work::get_hasher;
use crate::work::is_valid_proof_of_work;
use crate::work::mkhash_into;
use crate::work::Hasher;

type Parent = Channel<Message, Answer>;
type Child = Channel<Answer, Message>;

#[derive(Debug)]
pub enum Message {
  Shutdown,
  Challenge(Challenge),
}

#[derive(Debug)]
pub struct Worker {
  channel: Channel<Message, Answer>,
  handle: Option<JoinHandle<()>>,
}

impl Worker {
  pub fn new() -> Result<Self> {
    let (chan1, chan2): (Parent, Child) = Channel::duplex();

    let handle: JoinHandle<()> = Builder::new()
      .name("Miner".to_string())
      .spawn(Self::task(chan2))
      .expect("Worker thread failed to spawn");

    Ok(Self {
      channel: chan1,
      handle: Some(handle),
    })
  }

  pub fn send(&self, challenge: Challenge) {
    self.channel.send(Message::Challenge(challenge));
  }

  pub fn recv(&self) -> Option<Answer> {
    self.channel.try_recv()
  }

  fn task(channel: Channel<Answer, Message>) -> impl Fn() {
    move || {
      let mut hasher: Hasher = get_hasher();
      let mut output: H256 = H256::new();
      let mut rng: ThreadRng = thread_rng();

      'outer: loop {
        debug!("Waiting for challenge");

        let mut challenge: Challenge = match channel.recv() {
          Message::Challenge(challenge) => challenge,
          Message::Shutdown => break 'outer,
        };

        debug!("Received challenge: {:?}", challenge);

        let mut nonce: u64 = rng.gen_range(0, u64::MAX);

        'inner: loop {
          mkhash_into(
            &mut hasher,
            &mut output,
            &challenge.block_id,
            &challenge.peer_id,
            nonce,
          );

          if is_valid_proof_of_work(&output, challenge.difficulty) {
            debug!("Found nonce: {:?} -> {}", nonce, to_hex(&output));
            break 'inner;
          }

          match channel.try_recv() {
            Some(Message::Challenge(update)) => {
              debug!("Received update: {:?}", update);

              challenge = update;
              nonce = rng.gen_range(0, u64::MAX);
            }
            Some(Message::Shutdown) => {
              break 'outer;
            }
            None => {
              nonce = nonce.wrapping_add(1);
            }
          }
        }

        channel.send(Answer { challenge, nonce });
      }
    }
  }
}

impl Drop for Worker {
  fn drop(&mut self) {
    self.channel.send(Message::Shutdown);

    if let Some(handle) = self.handle.take() {
      if let Err(error) = handle.join() {
        error!("Handle failed to join: {:?}", error);
      }
    } else {
      error!("Handle is `None`");
    }
  }
}
