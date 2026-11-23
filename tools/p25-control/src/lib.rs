#![allow(dead_code)] // The upstream DSP/protocol modules also serve other decoder frontends.
include!(concat!(env!("OUT_DIR"), "/modules.rs"));

use dsp::nco::Nco;
use num_complex::Complex;
use p25::{ident::IdentTable, nid::NidIntegrityPolicy, receiver::ReceiverEvent, tsbk::{TsbkOpcode, TsbkPayload}, types::ChannelNumber};
use pipeline::{ChannelPipeline, Modulation, PipelineConfig, CC_SYNC_TIMEOUT_SAMPLES};

pub struct Decoder {
    radio: ChannelPipeline,
    mixer: Nco,
    bands: IdentTable,
    tdma: [bool; 16],
}

#[repr(C)]
#[derive(Default)]
pub struct Block {
    messages: i32,
    site_reports: i32,
    system: i32,
    rfss: i32,
    site: i32,
    nac: i32,
    talkgroup: i32,
    grant_count: i32,
}

#[repr(C)]
pub struct Grant { group: i32, hz: i32, nac: i32, encrypted: i32 }

#[unsafe(no_mangle)]
pub extern "C" fn p25_open(rate: u32, offset: i32) -> *mut Decoder {
    let Ok(radio) = ChannelPipeline::new(PipelineConfig {
        sample_rate: rate, modulation: Modulation::C4fm,
        nid_integrity: NidIntegrityPolicy::Strict, sync_timeout_samples: Some(CC_SYNC_TIMEOUT_SAMPLES),
    }) else { return std::ptr::null_mut() };
    Box::into_raw(Box::new(Decoder { radio, mixer: Nco::new(offset as f64, rate as f64), bands: IdentTable::new(), tdma: [false; 16] }))
}

// The receiver thread owns the handle and the input slice for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn p25_feed_grants(decoder: &mut Decoder, data: *const u8, length: usize, block: &mut Block, grants: *mut Grant, capacity: usize) {
    let grants = unsafe { std::slice::from_raw_parts_mut(grants, capacity) };
    *block = Block::default();
    for iq in unsafe { std::slice::from_raw_parts(data, length) }.chunks_exact(2) {
        let sample = Complex::new((iq[0] as f32 - 127.5) / 127.5, (iq[1] as f32 - 127.5) / 127.5);
        let Some(ReceiverEvent::Tsbk(message)) = decoder.radio.process_sample(decoder.mixer.shift(sample)) else { continue };
        block.messages += 1;
        if message.header.protected || message.header.manufacturer_id != 0 { continue; }
        if let TsbkPayload::IdentifierUpdate {identifier, ..} = message.payload {
            decoder.bands.update(&message);
            decoder.tdma[identifier as usize] = message.header.opcode == TsbkOpcode::IdentifierUpdateTdma;
        }
        let mut grant = |group: u16, ch: ChannelNumber, encrypted: bool| {
            if group == 0 || decoder.tdma[ch.identifier() as usize] { return; }
            let Some(freq) = decoder.bands.resolve_frequency(ch) else { return };
            let hz = freq.hz();
            if !((769000000..=775000000).contains(&hz) || (851000000..=861000000).contains(&hz)) { return; }
            if let Some(slot) = grants.get_mut(block.grant_count as usize) {
                *slot = Grant {group: group.into(), hz: hz as i32, nac: decoder.radio.current_nac().value().into(), encrypted: encrypted.into()};
                block.talkgroup = group.into();
                block.grant_count += 1;
            }
        };
        match message.payload {
            TsbkPayload::RfssStatusBroadcast { system_id, rfss_id, site_id, .. } => {
                block.site_reports += 1;
                block.system = system_id.value().into();
                block.rfss = rfss_id.value().into();
                block.site = site_id.value().into();
                block.nac = decoder.radio.current_nac().value().into();
            }
            TsbkPayload::GroupVoiceChannelGrant { channel, talkgroup, source } => {
                // At the pinned upstream revision opcode 0x00 fields start one byte early.
                // Bytes 2..7 on the wire are options, channel (BE16), group (BE16).
                let [options, ch_hi] = channel.value().to_be_bytes();
                let [ch_lo, tg_hi] = talkgroup.value().to_be_bytes();
                let tg_lo = (source.value() >> 16) as u8;
                grant(u16::from_be_bytes([tg_hi, tg_lo]), ChannelNumber::new(u16::from_be_bytes([ch_hi,ch_lo])), options & 0x40 != 0);
            }
            TsbkPayload::GroupVoiceChannelGrantUpdateExplicit { talkgroup, transmit_channel, service_options, .. } =>
                grant(talkgroup.value(), transmit_channel, service_options & 0x40 != 0),
            TsbkPayload::GroupVoiceChannelGrantUpdate { talkgroup_a, channel_a, talkgroup_b, channel_b } => {
                grant(talkgroup_a.value(), channel_a, false);
                if channel_a != channel_b || talkgroup_a != talkgroup_b { grant(talkgroup_b.value(), channel_b, false); }
            }
            _ => {}
        }
    }
}

mod voice;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn p25_close(decoder: *mut Decoder) {
    if !decoder.is_null() { drop(unsafe { Box::from_raw(decoder) }); }
}
