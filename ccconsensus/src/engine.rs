use anyhow::Result;
use sawtooth_sdk::consensus::engine::Engine;
use sawtooth_sdk::consensus::engine::Error;
use sawtooth_sdk::consensus::engine::StartupState;
use sawtooth_sdk::consensus::engine::Update;
use sawtooth_sdk::consensus::service::Service;
use std::sync::mpsc::Receiver;
use std::sync::mpsc::RecvTimeoutError;

use crate::node::PowConfig;
use crate::node::PowNode;

const ENGINE_NAME: &str = "PoW";
const ENGINE_VERSION: &str = "0.1";

#[derive(Debug, Default)]
#[repr(transparent)]
pub struct PowEngine {
  config: Option<PowConfig>,
}

impl PowEngine {
  pub const fn new() -> Self {
    Self { config: None }
  }

  pub const fn with_config(config: PowConfig) -> Self {
    Self {
      config: Some(config),
    }
  }
}

impl Engine for PowEngine {
  fn start(
    &mut self,
    updates: Receiver<Update>,
    service: Box<dyn Service>,
    startup: StartupState,
  ) -> Result<(), Error> {
    // Create a new PoW node, using the engine config if one exists.
    let mut node: PowNode = match self.config.take() {
      Some(config) => PowNode::with_config(service, config),
      None => PowNode::new(service),
    };

    // Initialize the PoW based on the current startup state received from the
    // validator - an error here is considered fatal and prevents startup.
    //
    // Note: Errors from this call don't propagate due to conflicting types,
    // this means we need to handle them explicity.
    if let Err(error) = node.initialize(startup) {
      error!("Init Error: {}", error);
      return Ok(());
    }

    loop {
      match updates.recv_timeout(node.config.update_recv_timeout) {
        Ok(update) => match node.handle_update(update) {
          Ok(true) => {}
          Ok(false) => break,
          Err(error) => error!("Update Error: {}", error),
        },
        Err(RecvTimeoutError::Disconnected) => {
          error!("Disconnected from validator");
          break;
        }
        Err(RecvTimeoutError::Timeout) => {}
      }

      if let Err(error) = node.try_publish() {
        error!("Publish Error: {}", error);
      }
    }

    Ok(())
  }

  fn name(&self) -> String {
    ENGINE_NAME.to_string()
  }

  fn version(&self) -> String {
    ENGINE_VERSION.to_string()
  }

  fn additional_protocols(&self) -> Vec<(String, String)> {
    Vec::new()
  }
}
