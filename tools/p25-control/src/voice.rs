use super::*;
use p25::{nid::DataUnit, voice::{control::{GroupVoiceTraffic, LinkControlOpcode}, crypto::CryptoAlgorithm}};
use vocoder::{ImbeDecoder, ReceivedFrame, SAMPLES_PER_FRAME};

pub struct Voice {
    pipeline: ChannelPipeline,
    mixer: Nco,
    codec: ImbeDecoder,
    group: u16,
    nac: u16,
    identified: bool,
    clear: bool,
    ended: bool,
}

#[repr(C)]
#[derive(Default)]
pub struct VoiceBlock { samples: i32, frames: i32, ended: i32, encrypted: i32 }

#[unsafe(no_mangle)]
pub extern "C" fn p25_voice_open(rate: u32, offset: i32, group: u16, nac: u16) -> *mut Voice {
    let Ok(pipeline) = ChannelPipeline::new(PipelineConfig { sample_rate: rate, modulation: Modulation::C4fm,
        nid_integrity: NidIntegrityPolicy::Strict, sync_timeout_samples: Some(24_000) }) else { return std::ptr::null_mut() };
    Box::into_raw(Box::new(Voice {pipeline, mixer: Nco::new(offset.into(), rate.into()), codec: ImbeDecoder::new(),
        group, nac, identified: false, clear: false, ended: false}))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn p25_voice_feed(v: &mut Voice, data: *const u8, length: usize, out: *mut i16, capacity: usize, result: &mut VoiceBlock) {
    *result = VoiceBlock::default();
    let pcm = unsafe {std::slice::from_raw_parts_mut(out, capacity)};
    for iq in unsafe {std::slice::from_raw_parts(data, length)}.chunks_exact(2) {
        if v.ended { break; }
        let sample = Complex::new((iq[0] as f32 - 127.5)/127.5, (iq[1] as f32 - 127.5)/127.5);
        let Some(event) = v.pipeline.process_sample(v.mixer.shift(sample)) else { continue };
        if v.pipeline.current_nac().value() != v.nac { v.clear = false; v.identified = false; continue; }
        match event {
            ReceiverEvent::Nid(nid) => match nid.data_unit {
                DataUnit::VoiceSimpleTerminator | DataUnit::VoiceLcTerminator => v.ended = true,
                DataUnit::VoiceHeader => { v.clear = false; v.identified = false; }
                _ => {}
            },
            ReceiverEvent::VoiceHeader(header) => {
                v.identified = header.talkgroup().value() == v.group;
                v.clear = header.algorithm() == CryptoAlgorithm::Unencrypted;
                result.encrypted = (!v.clear).into();
                v.ended = !v.identified || !v.clear;
            }
            ReceiverEvent::LinkControl(lc) if lc.protected() => { v.ended = true; result.encrypted = 1; }
            ReceiverEvent::LinkControl(lc) if lc.opcode() == LinkControlOpcode::GroupVoiceTraffic => {
                let traffic = GroupVoiceTraffic::new(lc);
                v.identified = traffic.talkgroup().value() == v.group;
                result.encrypted = traffic.service_options().protected().into();
                v.ended = !v.identified || result.encrypted != 0;
            }
            ReceiverEvent::CryptoControl(info) => {
                v.clear = info.algorithm() == CryptoAlgorithm::Unencrypted;
                result.encrypted = (!v.clear).into();
                v.ended = !v.clear;
            }
            ReceiverEvent::VoiceFrame(frame) => {
                result.frames += 1;
                if v.clear && v.identified {
                    let start = result.samples as usize;
                    if let Some(dest) = pcm.get_mut(start..start + SAMPLES_PER_FRAME) {
                        let mut audio: vocoder::AudioBuffer = [0.0; SAMPLES_PER_FRAME];
                        v.codec.decode(ReceivedFrame::from(&frame), &mut audio);
                        for (slot, sample) in dest.iter_mut().zip(audio) { *slot = sample as i16; }
                        result.samples += SAMPLES_PER_FRAME as i32;
                    }
                }
            }
            _ => {}
        }
    }
    result.ended = v.ended.into();
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn p25_voice_close(voice: *mut Voice) {
    if !voice.is_null() { drop(unsafe {Box::from_raw(voice)}); }
}
