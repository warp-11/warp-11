//! Analytic anchor for the full-scale cycle findings: total issue-iterations
//! of the benchmark view (padded 1408x800, maxIter 256) from the twin, so the
//! measured silicon numbers can be compared against physics: ideal lane-limit
//! cycles = total_issues / 104.

use warp11_host::mandel_frame_twin::frame_twin;

fn q4_28(v: f64) -> u32 {
    (v * f64::from(1u32 << 28)) as i64 as u32
}

#[test]
fn ideal_cycles_for_the_benchmark_view() {
    let (w, h) = (1408usize, 800usize);
    let frame = frame_twin(
        w, h, 256,
        q4_28(-2.5), q4_28(-1.0),
        q4_28(3.5 / 1400.0), q4_28(2.0 / 800.0),
    );
    let total_issues: u64 = frame.iter().map(|&n| n as u64 + 1).sum();
    let ideal_104 = total_issues / 104;
    println!(
        "total pixels {}  total issues {}  ideal cycles @104 lanes = {}",
        frame.len(), total_issues, ideal_104
    );
    println!(
        "vs measured: F# 715,938 ({:.1}% of ideal)   Kotlin 548,809 ({:.1}%)",
        ideal_104 as f64 / 715_938.0 * 100.0,
        ideal_104 as f64 / 548_809.0 * 100.0
    );
}

#[test]
fn candidate_views_vs_the_kotlin_number() {
    let ideal = |cx0: f64, cy0: f64, xspan: f64, yspan: f64| -> u64 {
        let frame = frame_twin(
            1408, 800, 256,
            q4_28(cx0), q4_28(cy0),
            q4_28(xspan / 1400.0), q4_28(yspan / 800.0),
        );
        frame.iter().map(|&n| n as u64 + 1).sum::<u64>() / 104
    };
    for (name, cx0, cy0, xs, ys) in [
        ("benchmark  x[-2.5,1.0] y[-1.0,1.0]", -2.5, -1.0, 3.5, 2.0),
        ("test-view  x[-2.5,1.0] y[-1.25,1.25]", -2.5, -1.25, 3.5, 2.5),
        ("classic    x[-2.25,0.75] y[-1.125,1.125]", -2.25, -1.125, 3.0, 2.25),
    ] {
        // probe-view ideal is the classic row; silicon measured 749,745 there
        let i = ideal(cx0, cy0, xs, ys);
        println!("{name}: ideal={i}  Kotlin 548,809 = {:.1}% of it", i as f64 / 548_809.0 * 100.0);
    }
}
