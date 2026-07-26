
# ComfortEyes: A Real-Time Screen Overlay System for Reducing Visual Fatigue via Invert-Shift Filtering

**Abstract**

Prolonged exposure to high-contrast digital displays is a leading cause of visual fatigue, commonly manifesting as eye strain, blurred vision, and headaches. Existing mitigation approaches — including blue light filtering, adaptive brightness, and dark mode — address luminance and color temperature but leave the fundamental issue of high-contrast edge rendering unresolved. We present ComfortEyes, a lightweight real-time screen overlay system that applies a dual-layer invert-shift filter directly over the desktop compositor, producing an e-ink-like display characteristic with enhanced text edge definition and reduced perceptual contrast. We further demonstrate that the invert-shift mechanism provides partial relief for users with astigmatism by introducing a compensating ghost image that attenuates the defocus artifact inherent to irregular corneal curvature, with first-order cancellation achievable through user calibration and only a perceptually negligible second-order residual remaining. ComfortEyes is implemented on Windows using layered window compositing and GDI+ rendering, with a reference macOS port architecture based on ScreenCaptureKit and Metal compute shaders.

---

## 1. Introduction

The prevalence of computer vision syndrome (CVS) has grown significantly with the widespread adoption of high-resolution, high-brightness LCD and OLED displays. Symptoms include ocular surface discomfort, transient refractive changes, and accommodative fatigue, affecting an estimated 50–90% of prolonged display users [1]. Current software interventions — f.lux, Night Shift, and OS-level dark modes — primarily target circadian disruption through blue light reduction but do not address the perceptual load imposed by sharp, high-contrast pixel rendering.

Astigmatism, affecting approximately one in three adults [2], further compounds this problem. An astigmatic eye focuses light along two distinct focal planes rather than one, producing a secondary ghost image offset from the primary image. On a high-contrast display, this ghost image is continuously visible to the visual cortex, demanding constant suppression effort and contributing disproportionately to fatigue.

We propose ComfortEyes, a system that addresses both the general display harshness problem and the astigmatism-specific ghost image problem through a unified invert-shift filtering approach applied as a real-time desktop overlay.

---

## 2. Related Work

**Blue light filtering.** Tools such as f.lux [3] and Windows Night Light shift the display color temperature toward warmer tones in the evening. While effective for circadian rhythm preservation, they do not reduce contrast or edge sharpness.

**Dark mode.** OS and application-level dark themes reduce average luminance but preserve high local contrast at text edges, leaving the primary driver of visual fatigue largely unchanged.

**Overlays and tinting.** Prior screen tinting approaches apply a uniform color wash over the display. These reduce brightness but introduce color distortion and do not enhance edge definition.

**Optical correction for astigmatism.** Corrective lenses and refractive surgery remain the primary treatments for astigmatism. No prior software system has attempted to compensate for the perceptual ghost image artifact in software at the display compositor level.

---

## 3. System Design

### 3.1 Overlay Architecture

ComfortEyes implements a full-screen transparent overlay window positioned above all application windows in the desktop z-order. On Windows, this is achieved using the `WS_EX_LAYERED` and `WS_EX_TRANSPARENT` extended window styles in combination with `UpdateLayeredWindow`, which writes a 32-bit ARGB bitmap directly into the Desktop Window Manager (DWM) compositor with per-pixel alpha blending. The overlay is excluded from screen capture using `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` and passes all mouse and keyboard events through to underlying windows.

### 3.2 Invert-Shift Filter

The core filtering operation consists of two layers composited over the live desktop:

**Layer 1 (base):** The desktop as rendered by the OS compositor, weighted by β.

**Layer 2 (invert-shift):** A spatially offset, color-inverted copy of the desktop, weighted by (1 − β).

Formally, for a pixel at position (x, y) with color value I(x, y), the output O(x, y) is:

```
O(x, y) = β · I(x, y) + (1 − β) · (1 − I(x + Δx, y + Δy))
```

where Δx and Δy are the horizontal and vertical shift parameters, and β ∈ [0, 1] is the blend factor.

### 3.3 Parameter Effects

**Shift (Δx, Δy).** A zero shift produces uniform dimming with color inversion. Non-zero shift values introduce edge enhancement: at text boundaries, the offset inverted layer partially cancels the original signal on one side of the edge and reinforces it on the other, producing a sharpening effect analogous to unsharp masking. Small values (Δx, Δy ∈ {2, 3, 4}) produce a subtle 3D relief effect on text that users report as easier to focus on.

**Blend (β).** Higher blend values increase the relative weight of the original image, reducing the dimming effect. Values of β close to 1 preserve the original display appearance with minimal filtering, while lower values deepen the e-ink-like effect.

---

## 4. Astigmatism Compensation

### 4.1 Ghost Image Model

In an astigmatic eye, the point spread function (PSF) is elongated along the axis of maximum refractive error, producing a secondary image displaced from the primary by a vector (δx, δy) in retinal coordinates. The perceived image P at the retina is approximately:

```
P(x, y) = α · I(x, y) + (1 − α) · I(x + δx, y + δy)
```

where α is the relative weight of the primary image, and (1 − α) is the ghost image weight determined by the degree of astigmatism.

### 4.2 Compensation via Invert-Shift

ComfortEyes presents output O instead of the raw image I. When the shift parameters are tuned such that (Δx, Δy) = (δx, δy), the astigmatic eye perceives:

```
P'(x, y) = α · O(x, y) + (1 − α) · O(x + δx, y + δy)
```

Substituting the definition of O:

```
P'(x, y) = α · [β · I(x, y) + (1 − β) · (1 − I(x + δx, y + δy))]
          + (1 − α) · [β · I(x + δx, y + δy) + (1 − β) · (1 − I(x + 2δx, y + 2δy))]
```

Collecting the first-order ghost term I(x + δx, y + δy):

```
coefficient = −α(1 − β) + (1 − α)β
            = β − α
```

At the optimal calibration point **β = α**, this coefficient vanishes and the first-order ghost image **cancels exactly**. The remaining second-order residual is:

```
−(1 − α)(1 − β) · I(x + 2δx, y + 2δy) = −(1 − α)² · I(x + 2δx, y + 2δy)
```

This residual ghost appears at **double the original offset** with weight **(1 − α)²**. For typical mild-to-moderate astigmatism (α ≈ 0.8), the original ghost weight is 0.2, while the second-order residual weight is only 0.04 — a fivefold reduction. The doubled spatial offset further reduces its perceptual salience, making the residual negligible in practice.

### 4.3 User Calibration

Since the optimal parameters vary by individual, ComfortEyes exposes Δx, Δy, and β as user-adjustable sliders. Users with astigmatism are instructed to incrementally adjust these values until text appears most comfortable. At the optimal setting β = α, first-order ghost cancellation is achieved. The blend parameter β therefore serves a dual role: controlling the overall display character while simultaneously encoding the user's astigmatic primary image weight α.

---

## 5. Implementation

### 5.1 Windows

ComfortEyes is implemented in C# using WinForms and GDI+ for bitmap manipulation. The filter runs on a dedicated background thread at approximately 30 fps, capturing the desktop via `BitBlt`, applying the invert-shift transformation, and pushing the result to the overlay window via `UpdateLayeredWindow`. The settings panel exposes Δx, Δy, and β as real-time sliders.

### 5.2 macOS (Reference Architecture)

A macOS port replaces the Windows-specific components as follows: screen capture via ScreenCaptureKit, GPU-accelerated invert-shift processing via a Metal compute shader, and overlay rendering via an `NSWindow` with `isOpaque = false` and `CAMetalLayer`. This architecture moves the filter pipeline entirely to the GPU, reducing CPU load compared to the Windows implementation.

### 5.3 Trial Mode and the 20-20-20 Rule

The trial version of ComfortEyes incorporates a built-in break reminder aligned with the clinically recommended 20-20-20 rule [4]: every 20 minutes, the overlay pauses and prompts the user to rest their eyes for 20 seconds by looking at an object 20 feet away. The user resumes operation by pressing a Resume button, with no limit on the number of cycles.

---

## 6. Results and User Feedback

Preliminary feedback from early users indicates:

- Reduced end-of-day eye fatigue reported by the majority of users after one week of daily use
- Users with mild to moderate astigmatism reported improved text readability at shift values between 2 and 4 pixels
- No measurable impact on application performance or display latency at the tested frame rates
- Conflict observed with NVIDIA Digital Vibrance at non-default settings; resolved by setting Digital Vibrance to 50%

Formal user studies are ongoing and will be reported in a subsequent publication.

---

## 7. Conclusion

We have presented ComfortEyes, a real-time screen overlay system that reduces visual fatigue through invert-shift compositing. The system addresses both the general problem of harsh display rendering and the specific perceptual burden imposed by astigmatic ghost images through a unified filter with two user-adjustable parameters. The mathematical analysis shows that at the optimal blend setting β = α, the first-order ghost image cancels exactly, leaving only a second-order residual at double the spatial offset with weight (1 − α)² — perceptually negligible for typical astigmatism severity. The lightweight implementation imposes negligible system overhead and is suitable for always-on use. Future work includes a formal clinical evaluation of astigmatism compensation efficacy, a GPU-accelerated Windows backend, and a macOS release via the Mac App Store.

---

## References

[1] Blehm, C., Vishnu, S., Khattak, A., Mitra, S., & Yee, R. W. (2005). Computer vision syndrome: a review. *Survey of Ophthalmology*, 50(3), 253–262.

[2] Hashemi, H., Fotouhi, A., Yekta, A., Pakzad, R., Ostadimoghaddam, H., & Khabazkhoob, M. (2018). Global and regional estimates of prevalence of refractive errors. *Journal of Current Ophthalmology*, 30(1), 3–22.

[3] Lam, R. W. (2018). Biological effects of light. In *Comprehensive Handbook of Clinical Health Psychology*. Wiley.

[4] American Optometric Association. (2023). Computer vision syndrome. Retrieved from https://www.aoa.org