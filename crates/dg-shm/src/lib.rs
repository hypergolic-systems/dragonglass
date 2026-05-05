pub mod layout;
pub mod shm;

pub use shm::{
    default_shm_path, shm_path_for_session, InputEvent, InputRingReader, ShmWriter, StreamRect,
};

#[cfg(test)]
pub use shm::ShmReader;
