/*
This is free and unencumbered software released into the public domain.

Anyone is free to copy, modify, publish, use, compile, sell, or
distribute this software, either in source code form or as a compiled
binary, for any purpose, commercial or non-commercial, and by any
means.

In jurisdictions that recognize copyright laws, the author or authors
of this software dedicate any and all copyright interest in the
software to the public domain. We make this dedication for the benefit
of the public at large and to the detriment of our heirs and
successors. We intend this dedication to be an overt act of
relinquishment in perpetuity of all present and future rights to this
software under copyright law.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.

For more information, please refer to <https://unlicense.org/>
*/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Cooperative
{
    // ------------------------------------------------------------------------
    // Public surface: a task is an async method that takes a SchedulerYield
    // helper. The user calls `await y.Yield()` at cooperative points, and
    // `await y.ToMain()` / `await y.ToBackground()` to control thread
    // affinity (desktop strategy only; no-ops under the web strategy).
    // Pass a function of this type to CooperativeScheduler.Run
    // ------------------------------------------------------------------------
    public delegate Awaitable CooperativeTask(SchedulerYield y);
    
    // This delegate is for internal use only, no use by the outside user
    public delegate void Completer(Action continuation);

    /// <summary>
    /// Strategy-agnostic yield helper handed to user tasks.
    ///
    /// Yield() is the cooperative yield point. Under the web strategy it
    /// checks the frame budget and may complete synchronously or defer to
    /// next frame. Under the desktop strategy it is a no-op, since real
    /// threads (managed via ToBackground/ToMain) handle concurrency there.
    ///
    /// ToBackground() and ToMain() are explicit thread-affinity requests.
    /// Under the desktop strategy they hop to the named thread. Under the
    /// web strategy they are no-ops (everything runs on the main thread).
    /// Use these to control which thread a particular block of work runs on.
    /// </summary>
    public abstract class SchedulerYield
    {
        public abstract SchedulerAwaitable Yield();
        // Note here that an if we are in the awaitable context and a child function
        // calls ToBackground() or ToMain(), this change will not propagate to the caller
        // function. However, the thread from the parent function that used ToBackground()
        // or ToMain() will propagate to the child call. Keep this in mind when writing your
        // scripts.
        public abstract SchedulerAwaitable ToBackground();
        public abstract SchedulerAwaitable ToMain();
    }

    public readonly struct SchedulerAwaitable {
        private readonly Completer Completer;

        public SchedulerAwaitable(Completer completer) {
            Completer = completer;
        }

        public SchedulerAwaiter GetAwaiter() => new SchedulerAwaiter(Completer);
        
        public static readonly SchedulerAwaitable Noop = new SchedulerAwaitable(null); 
    }

    public readonly struct SchedulerAwaiter : INotifyCompletion {
        private readonly Completer Completer;

        public SchedulerAwaiter(Completer c) { Completer = c; }

        public bool IsCompleted => Completer == null;

        public void OnCompleted(Action continuation) => Completer(continuation);

        public void GetResult() { }
    }

    // ------------------------------------------------------------------------
    // MonoBehaviour that drives tasks. One instance lives in the scene.
    // ------------------------------------------------------------------------
    public class CooperativeScheduler : MonoBehaviour
    {
        [Tooltip("Milliseconds of budget per frame for cooperative tasks (web strategy). Recommended range is 4-8 ms.")]
        public float FrameBudgetMs = 4f;

        [Tooltip("If true, desktop builds and the editor will use the web (frame-budget) " +
                 "strategy instead of Awaitable background threads. Useful for parity testing.")]
        public bool ForceWebStrategy = false;

        // Deferred continuations waiting for the next frame (web strategy).
        private readonly Queue<Action> Deferred = new();

        // The real-time clock used to measure the frame budget. Using
        // Time.realtimeSinceStartupAsDouble so we're not fooled by timeScale.
        private double FrameStart;

        private bool UseWebStrategy
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return true;
#else
                return ForceWebStrategy;
#endif
            }
        }

        private SchedulerYield Yielder;
        
        public void Awake() {
            Yielder = UseWebStrategy ? new WebYield(this) : new DesktopYield();
        }

        private void Update() {
            if (UseWebStrategy) {
                FrameStart = Time.realtimeSinceStartupAsDouble;
                
                while (!BudgetExhausted() && Deferred.TryDequeue(out Action k)) {
                    k();
                }
            }
        }

        private bool BudgetExhausted() {
            double elapsedMs = (Time.realtimeSinceStartupAsDouble - FrameStart) * 1000.0;
            return elapsedMs >= FrameBudgetMs;
        }

        // --------------------------------------------------------------------
        // Public entry point. Kicks off a cooperative task.
        //
        // The task body begins running synchronously on the caller's thread
        // (typically the main thread, since Run is usually called from
        // MonoBehaviour code). Under the desktop strategy the task is
        // responsible for calling ToBackground()/ToMain() itself to control
        // thread affinity. Under the web strategy there is only the main
        // thread, so those calls are no-ops.
        // --------------------------------------------------------------------
        public Awaitable Run(CooperativeTask task) {
            return task(Yielder);
        }

        // --------------------------------------------------------------------
        // Web strategy: Yield checks budget, possibly defers to next frame.
        // --------------------------------------------------------------------
        private sealed class WebYield : SchedulerYield
        {
            private readonly CooperativeScheduler Scheduler;
            
            public WebYield(CooperativeScheduler scheduler) { Scheduler = scheduler; }

            public override SchedulerAwaitable Yield() {
                if (Scheduler.BudgetExhausted()) {
                    return new SchedulerAwaitable((continuation) => Scheduler.Deferred.Enqueue(continuation));
                } else {
                    // Fast path: there's still budget available so keep on going
                    return SchedulerAwaitable.Noop;
                }
            }

            // Under the web strategy everything already runs on the main
            // thread, so both thread-affinity calls are no-ops.
            public override SchedulerAwaitable ToBackground() => SchedulerAwaitable.Noop;
            public override SchedulerAwaitable ToMain() => SchedulerAwaitable.Noop;
        }

        // --------------------------------------------------------------------
        // Desktop strategy: Yield is a no-op; the task controls thread
        // affinity explicitly via ToBackground/ToMain. The hop instances
        // wrap Unity's special BackgroundThreadAsync/MainThreadAsync awaiters
        // --------------------------------------------------------------------
        private sealed class DesktopYield : SchedulerYield
        {
            public override SchedulerAwaitable Yield() => SchedulerAwaitable.Noop;

            public override SchedulerAwaitable ToBackground()
                => new SchedulerAwaitable((continuation) => Awaitable.BackgroundThreadAsync().GetAwaiter().OnCompleted(continuation));

            public override SchedulerAwaitable ToMain()
                => new SchedulerAwaitable((continuation) => Awaitable.MainThreadAsync().GetAwaiter().OnCompleted(continuation));
        }
    }
}