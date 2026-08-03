use std::slice;

const ABI_VERSION: u32 = 2;

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BstringsHit {
    pub start: u32,
    pub length: u32,
}

#[unsafe(no_mangle)]
pub extern "C" fn bstrings_abi_version() -> u32 {
    ABI_VERSION
}

/// Returns 2 for AVX2, 1 for SSE2, and 0 for the scalar kernel.
#[unsafe(no_mangle)]
pub extern "C" fn bstrings_ascii_kernel() -> u32 {
    selected_ascii_kernel()
}

/// Finds inclusive-range byte runs in a caller-owned buffer.
///
/// # Safety
///
/// `data` must address `data_length` readable bytes, unless `data_length` is zero.
/// `output` must address `output_capacity` writable `BstringsHit` values unless the
/// capacity is zero. `required_length` must point to writable memory. Status 2 means
/// the buffer was too small; `required_length` still contains the exact required size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bstrings_find_ascii_hits(
    data: *const u8,
    data_length: usize,
    min_length: i32,
    max_length: i32,
    min_char: u8,
    max_char: u8,
    output: *mut BstringsHit,
    output_capacity: usize,
    required_length: *mut usize,
) -> i32 {
    if required_length.is_null()
        || (data.is_null() && data_length != 0)
        || (output.is_null() && output_capacity != 0)
        || data_length > u32::MAX as usize
    {
        return 1;
    }

    unsafe {
        required_length.write(0);
    }

    let bytes = if data_length == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(data, data_length) }
    };
    let output = if output_capacity == 0 {
        &mut []
    } else {
        unsafe { slice::from_raw_parts_mut(output, output_capacity) }
    };
    let mut sink = BufferedHitSink {
        output,
        required: 0,
    };
    scan_ascii(bytes, min_length, max_length, min_char, max_char, &mut sink);
    unsafe {
        required_length.write(sink.required);
    }
    if sink.required > output_capacity {
        2
    } else {
        0
    }
}

fn selected_ascii_kernel() -> u32 {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    {
        if std::is_x86_feature_detected!("avx2") {
            return 2;
        }
        if std::is_x86_feature_detected!("sse2") {
            return 1;
        }
    }

    0
}

pub fn find_ascii_hits(
    data: &[u8],
    min_length: i32,
    max_length: i32,
    min_char: u8,
    max_char: u8,
) -> Vec<BstringsHit> {
    let mut hits = Vec::with_capacity((data.len() / 64).min(4096));
    scan_ascii(data, min_length, max_length, min_char, max_char, &mut hits);
    hits
}

trait HitSink {
    fn push_hit(&mut self, hit: BstringsHit);
}

impl HitSink for Vec<BstringsHit> {
    #[inline(always)]
    fn push_hit(&mut self, hit: BstringsHit) {
        self.push(hit);
    }
}

struct BufferedHitSink<'a> {
    output: &'a mut [BstringsHit],
    required: usize,
}

#[derive(Clone, Copy)]
struct ScanConfig {
    min_length: i32,
    max_length: i32,
    min_char: u8,
    max_char: u8,
}

impl HitSink for BufferedHitSink<'_> {
    #[inline(always)]
    fn push_hit(&mut self, hit: BstringsHit) {
        if self.required < self.output.len() {
            self.output[self.required] = hit;
        }
        self.required += 1;
    }
}

fn scan_ascii<S: HitSink>(
    data: &[u8],
    min_length: i32,
    max_length: i32,
    min_char: u8,
    max_char: u8,
    hits: &mut S,
) {
    if data.is_empty() || min_char > max_char {
        return;
    }
    let config = ScanConfig {
        min_length,
        max_length,
        min_char,
        max_char,
    };

    let mut string_start = None;
    let mut position = 0;

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    {
        if std::is_x86_feature_detected!("avx2") && data.len() >= 32 {
            position = unsafe { scan_avx2(data, position, config, &mut string_start, hits) };
        }

        if std::is_x86_feature_detected!("sse2") && data.len().saturating_sub(position) >= 16 {
            position = unsafe { scan_sse2(data, position, config, &mut string_start, hits) };
        }
    }

    for (index, value) in data[position..].iter().copied().enumerate() {
        let absolute = position + index;
        process_character(
            value >= min_char && value <= max_char,
            absolute,
            &mut string_start,
            min_length,
            max_length,
            hits,
        );
    }

    if let Some(start) = string_start {
        add_hit(start, data.len(), min_length, max_length, hits);
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
unsafe fn scan_avx2<S: HitSink>(
    data: &[u8],
    mut position: usize,
    config: ScanConfig,
    string_start: &mut Option<usize>,
    hits: &mut S,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    let minimum = _mm256_set1_epi8(config.min_char as i8);
    let maximum = _mm256_set1_epi8(config.max_char as i8);
    while position + 32 <= data.len() {
        let block = unsafe { _mm256_loadu_si256(data.as_ptr().add(position).cast()) };
        let at_least_minimum = _mm256_cmpeq_epi8(_mm256_max_epu8(block, minimum), block);
        let at_most_maximum = _mm256_cmpeq_epi8(_mm256_min_epu8(block, maximum), block);
        let valid = _mm256_and_si256(at_least_minimum, at_most_maximum);
        let mask = _mm256_movemask_epi8(valid) as u32;
        if mask == 0 {
            if let Some(start) = string_start.take() {
                add_hit(start, position, config.min_length, config.max_length, hits);
            }
        } else if mask == u32::MAX {
            if string_start.is_none() {
                *string_start = Some(position);
            }
        } else {
            process_validity_mask(
                mask,
                32,
                position,
                string_start,
                config.min_length,
                config.max_length,
                hits,
            );
        }
        position += 32;
    }
    position
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn scan_sse2<S: HitSink>(
    data: &[u8],
    mut position: usize,
    config: ScanConfig,
    string_start: &mut Option<usize>,
    hits: &mut S,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    let minimum = _mm_set1_epi8(config.min_char as i8);
    let maximum = _mm_set1_epi8(config.max_char as i8);
    while position + 16 <= data.len() {
        let block = unsafe { _mm_loadu_si128(data.as_ptr().add(position).cast()) };
        let at_least_minimum = _mm_cmpeq_epi8(_mm_max_epu8(block, minimum), block);
        let at_most_maximum = _mm_cmpeq_epi8(_mm_min_epu8(block, maximum), block);
        let valid = _mm_and_si128(at_least_minimum, at_most_maximum);
        let mask = _mm_movemask_epi8(valid) as u32;
        if mask == 0 {
            if let Some(start) = string_start.take() {
                add_hit(start, position, config.min_length, config.max_length, hits);
            }
        } else if mask == 0xffff {
            if string_start.is_none() {
                *string_start = Some(position);
            }
        } else {
            process_validity_mask(
                mask,
                16,
                position,
                string_start,
                config.min_length,
                config.max_length,
                hits,
            );
        }
        position += 16;
    }
    position
}

#[inline(always)]
fn process_validity_mask<S: HitSink>(
    valid_mask: u32,
    width: u32,
    block_start: usize,
    string_start: &mut Option<usize>,
    min_length: i32,
    max_length: i32,
    hits: &mut S,
) {
    let mut bit = 0;
    while bit < width {
        let remaining = valid_mask >> bit;
        if remaining & 1 == 0 {
            if let Some(start) = string_start.take() {
                add_hit(
                    start,
                    block_start + bit as usize,
                    min_length,
                    max_length,
                    hits,
                );
            }
            if remaining == 0 {
                return;
            }
            bit += remaining.trailing_zeros();
            continue;
        }

        if string_start.is_none() {
            *string_start = Some(block_start + bit as usize);
        }
        let run_length = (!remaining).trailing_zeros().min(width - bit);
        bit += run_length;
        if bit < width
            && let Some(start) = string_start.take()
        {
            add_hit(
                start,
                block_start + bit as usize,
                min_length,
                max_length,
                hits,
            );
        }
    }
}

#[inline(always)]
fn process_character<S: HitSink>(
    valid: bool,
    position: usize,
    string_start: &mut Option<usize>,
    min_length: i32,
    max_length: i32,
    hits: &mut S,
) {
    if valid {
        if string_start.is_none() {
            *string_start = Some(position);
        }
    } else if let Some(start) = string_start.take() {
        add_hit(start, position, min_length, max_length, hits);
    }
}

#[inline(always)]
fn add_hit<S: HitSink>(start: usize, end: usize, min_length: i32, max_length: i32, hits: &mut S) {
    let length = end - start;
    if min_length > 0 && length < min_length as usize {
        return;
    }
    let actual_length = if max_length > 0 {
        length.min(max_length as usize)
    } else {
        length
    };
    hits.push_hit(BstringsHit {
        start: start as u32,
        length: actual_length as u32,
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_offsets_ranges_and_truncated_runs() {
        assert_eq!(
            find_ascii_hits(b"\x01Alpha123\x02Beta!\x1f", 4, -1, 0x20, 0x7e),
            vec![
                BstringsHit {
                    start: 1,
                    length: 8,
                },
                BstringsHit {
                    start: 10,
                    length: 5,
                },
            ]
        );
        assert_eq!(
            find_ascii_hits(b"AB12-34Z", 2, 3, b'0', b'9'),
            vec![
                BstringsHit {
                    start: 2,
                    length: 2,
                },
                BstringsHit {
                    start: 5,
                    length: 2,
                },
            ]
        );
        assert_eq!(
            find_ascii_hits(b"ABCDEFGHIJ\x01", 2, 3, 0x20, 0x7e),
            vec![BstringsHit {
                start: 0,
                length: 3,
            }]
        );
    }

    #[test]
    fn dispatched_kernel_matches_scalar_reference() {
        let ranges = [(0, 0), (0x20, 0x7e), (0x30, 0x39), (0x80, 0xff), (0, 0xff)];
        let mut state = 0xB57A_1A65_D1CE_F00Du64;
        for length in 0..4097 {
            let mut data = vec![0u8; length];
            for value in &mut data {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                *value = state as u8;
            }
            let min_length = (state as i32 % 16) + 1;
            let max_length = if state.is_multiple_of(3) {
                -1
            } else {
                ((state >> 8) as i32 % 32).abs() + 1
            };
            for (minimum, maximum) in ranges {
                let expected = scalar_reference(&data, min_length, max_length, minimum, maximum);
                assert_eq!(
                    expected,
                    find_ascii_hits(&data, min_length, max_length, minimum, maximum),
                    "length={length}, range={minimum:02X}-{maximum:02X}"
                );
            }
        }
    }

    #[test]
    fn ffi_reports_required_capacity_and_fills_the_exact_buffer() {
        let data = b"\0alpha\0beta\0";
        let mut output = [BstringsHit {
            start: 0,
            length: 0,
        }; 2];
        let mut required = 0;
        let status = unsafe {
            bstrings_find_ascii_hits(
                data.as_ptr(),
                data.len(),
                4,
                -1,
                0x20,
                0x7e,
                output.as_mut_ptr(),
                1,
                &mut required,
            )
        };
        assert_eq!(2, status);
        assert_eq!(2, required);

        let status = unsafe {
            bstrings_find_ascii_hits(
                data.as_ptr(),
                data.len(),
                4,
                -1,
                0x20,
                0x7e,
                output.as_mut_ptr(),
                output.len(),
                &mut required,
            )
        };
        assert_eq!(0, status);
        assert_eq!(2, required);
        assert_eq!(
            output,
            [
                BstringsHit {
                    start: 1,
                    length: 5,
                },
                BstringsHit {
                    start: 7,
                    length: 4,
                },
            ]
        );
    }

    fn scalar_reference(
        data: &[u8],
        min_length: i32,
        max_length: i32,
        min_char: u8,
        max_char: u8,
    ) -> Vec<BstringsHit> {
        if min_char > max_char {
            return Vec::new();
        }
        let mut hits = Vec::new();
        let mut start = None;
        for position in 0..=data.len() {
            let valid =
                position < data.len() && data[position] >= min_char && data[position] <= max_char;
            process_character(
                valid, position, &mut start, min_length, max_length, &mut hits,
            );
        }
        hits
    }
}
