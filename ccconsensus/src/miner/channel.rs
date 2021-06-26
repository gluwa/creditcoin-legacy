use crossbeam_channel::unbounded;
use crossbeam_channel::Receiver;
use crossbeam_channel::Sender;
use crossbeam_channel::TryRecvError;

#[derive(Clone, Debug)]
pub struct Channel<T, U> {
  tx: Sender<T>,
  rx: Receiver<U>,
}

impl<T, U> Channel<T, U>
where
  T: Send + Sync + 'static,
  U: Send + Sync + 'static,
{
  pub fn duplex() -> (Channel<T, U>, Channel<U, T>) {
    let (tx_p, rx_p): (Sender<T>, Receiver<T>) = unbounded();
    let (tx_c, rx_c): (Sender<U>, Receiver<U>) = unbounded();

    let chan1: Channel<T, U> = Channel { tx: tx_p, rx: rx_c };
    let chan2: Channel<U, T> = Channel { tx: tx_c, rx: rx_p };

    (chan1, chan2)
  }

  pub fn send(&self, message: T) {
    self.tx.send(message).expect("Channel Disconnected (send)");
  }

  pub fn recv(&self) -> U {
    self.rx.recv().expect("Channel Disconnected (recv)")
  }

  pub fn try_recv(&self) -> Option<U> {
    match self.rx.try_recv() {
      Ok(message) => Some(message),
      Err(TryRecvError::Empty) => None,
      Err(TryRecvError::Disconnected) => {
        panic!("Channel Disconnected (try_recv)");
      }
    }
  }
}
