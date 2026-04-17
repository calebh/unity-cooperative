# unity-cooperative

A cooperative async scheduler for Unity web and desktop

This script uses the async functionality built into C# and the Awaitable class available in Unity to allow for easy multi-threading on both the web and the desktop. The primary use case of this library is for web-only Unity games or web and desktop Unity games.

On the web platform, Unity does not currently support C# multi-threading, with the exception of threads run by the Burst compiler. This is unfortunate, as not all applications fit nicely inside of what Burst can do. To work around this issue, we implement a CooperativeScheduler MonoBehaviour with a Run method to schedule long running tasks. The function passed to the Run method should routinely yield, giving the scheduler the opportunity to defer the task to the next frame. The scheduler keeps track of a time budget, allowing tasks to be deferred appropriately. The net result is long running tasks can be run on the main thread on the web with minimal changes to the architecture of your code.

On the desktop platform, the Yield() operation is a no-op. Instead, we provide ToBackground() and ToMain() methods, which will move the task to a background thread, or back to the main thread. This precisely matches the behavior provided by Awaitable in Unity (under the hood we use the Awaitable API). Note that on the web platform, ToBackground() and ToMain() are no-ops.

To summarize, you can write long running tasks using our API that will work simultaneously on both Unity web and Unity desktop. On Unity web, your code will run on the main thread, and may be deferred by the scheduler. On Unity desktop, your code will run on true background threads - the same as Awaitable.

Here is a sketch of an example use of our library:

```cs
public void Foo() {
	// Somehow get your hands on the CooperativeScheduler MonoBehaviour. Only one of these scripts should be running per scene
	// You could assign a CooperativeScheduler via the inspector or using a singleton or dependency injection system
	CooperativeScheduler scheduler = ...; 
	scheduler.Run(MyTask);
}

private async Awaitable MyTask(SchedulerYield yielder) {
	// The yielder object is provided by our library and has three primary methods:
	// ToBackground() - move the current task to a background thread on desktop. No-op on web
	// ToMain() - move the current task to the main thread on desktop. No-op on web
	// Yield() - Co-operate with the scheduler. If there's time left in the budget, the task will continue to run. Otherwise, suspend the task until the next frame. No-op on desktop

	// Example:
	await yielder.ToBackground();
	var myResults = await ExpensiveOperation(yielder);
	await yielder.ToMain();
	// Do some more stuff, this time on the main thread
	// ...
	// Switch to background again
	await yielder.ToBackground();
	await ExpensiveOperation2(yielder, myResults);
	// Do some more stuff, still on the background thread here
}

private async Awaitable<List<Vector3Int>> ExpensiveOperation(SchedulerYield yielder) {
	List<Vector3Int> ret = new List<Vector3Int>();
	for (int x = 0; x < 10000; x++) {
		// In your real code, look for loops from which it is okay to co-operatively yielder to the scheduler
		ret.Add(new Vector3Int(x, x, x));
		await yielder.Yield();
	}

	return ret;
}

private async Awaitable ExpensiveOperation2(SchedulerYield yielder, List<Vector3Int> data) {
	foreach (var pos in data) {
		// Do some more computation here
		// Co-operatively yield
		await yielder.Yield();
	}

	await yielder.ToMain();

	// Do some stuff on the main thread
	// Note that when we return to MyTask, control will be on the background thread (see gotcha note below)
}
```

## Gotcha with ToBackground() and ToMain()

Note that on the desktop platform, when an async child function is called, it will start running on the parent's thread. If the child function changes its thread using ToBackground() or ToMain(), this change will not propogate to the parent function once the child function returns. This matches the behaviour of the ordinary Awaitable class in Unity.