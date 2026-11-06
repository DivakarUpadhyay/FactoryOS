"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Camera } from "lucide-react";
import { Button, Modal } from "./ui";

type Detector = { detect: (source: HTMLVideoElement) => Promise<{ rawValue: string }[]> };

/**
 * Camera barcode / QR scanner for phones, tablets and laptops. Uses the browser's own BarcodeDetector where it exists
 * (Android, ChromeOS, macOS) and otherwise the ZXing library, loaded only when the camera opens. USB and Bluetooth
 * scanners need none of this: they type into the focused box and press Enter.
 */
export function CameraScan({ onCode, label = "Scan with camera" }: { onCode: (code: string) => void; label?: string }) {
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const video = useRef<HTMLVideoElement | null>(null);
  const stop = useRef<() => void>(() => undefined);

  const close = useCallback(() => {
    stop.current();
    setOpen(false);
  }, []);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setError(null);
    (async () => {
      if (!navigator.mediaDevices?.getUserMedia) {
        setError("This browser cannot use the camera here. Use https, or type or scan the code into the box.");
        return;
      }
      const el = video.current!;
      const found = (code: string) => {
        if (cancelled) return;
        cancelled = true;
        stop.current();
        setOpen(false);
        onCode(code.trim());
      };
      try {
        const native = (globalThis as { BarcodeDetector?: new (o?: { formats?: string[] }) => Detector }).BarcodeDetector;
        if (native) {
          const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } }, audio: false });
          if (cancelled) { stream.getTracks().forEach((t) => t.stop()); return; }
          el.srcObject = stream;
          await el.play();
          const detector = new native();
          const timer = setInterval(async () => {
            try {
              const codes = await detector.detect(el);
              if (codes[0]?.rawValue) found(codes[0].rawValue);
            } catch { /* frame not ready */ }
          }, 250);
          stop.current = () => { clearInterval(timer); stream.getTracks().forEach((t) => t.stop()); };
        } else {
          const { BrowserMultiFormatReader } = await import("@zxing/browser");
          if (cancelled) return;
          const controls = await new BrowserMultiFormatReader().decodeFromConstraints(
            { video: { facingMode: { ideal: "environment" } }, audio: false }, el, (result) => { if (result) found(result.getText()); });
          stop.current = () => controls.stop();
        }
      } catch (e) {
        setError(e instanceof DOMException && e.name === "NotAllowedError"
          ? "Camera permission was refused. Allow the camera for this site in the browser, then try again."
          : e instanceof DOMException && e.name === "NotFoundError" ? "No camera was found on this device." : "The camera could not be started.");
      }
    })();
    return () => { cancelled = true; stop.current(); };
  }, [open, onCode]);

  return (
    <>
      <Button type="button" variant="outline" onClick={() => setOpen(true)} aria-label={label} title={label}><Camera size={16} /><span className="hidden sm:inline">Camera</span></Button>
      <Modal open={open} title={label} onClose={close}>
        {error ? <p className="text-sm text-bad">{error}</p> : (
          <div className="space-y-3">
            <div className="relative overflow-hidden rounded-lg bg-black">
              <video ref={video} className="aspect-video w-full object-cover" muted playsInline />
              <div className="pointer-events-none absolute inset-x-[15%] top-1/2 h-0.5 -translate-y-1/2 animate-pulse bg-brand-2 shadow-[0_0_12px_var(--brand-2)]" />
            </div>
            <p className="text-xs text-muted">Hold the barcode or QR code inside the frame. It is read automatically.</p>
          </div>
        )}
      </Modal>
    </>
  );
}
