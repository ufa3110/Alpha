using System;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Drawing;
using System.Globalization;

namespace Alpha
{
	/// <summary>
	/// All work is shamelessly leached and cobbled together. 
	///		Follower: 13413j1j13j5315n13
	///		Terrain: mm3141
	///		Pathfinding: juhgiyo
	///	I'm just linking things together and doing silly experiments. 
	/// </summary>
	internal class AlphaCore : BaseSettingsPlugin<AlphaSettings>
	{
		internal static AlphaCore instance;
		private Random random = new Random();
		private Camera Camera => GameController.Game.IngameState.Camera;		
		private Dictionary<uint, Entity> areaTransitions = new Dictionary<uint, Entity>();
		
		private Vector3 lastTargetPosition;
		private Vector3 lastPlayerPosition;
		private Entity followTarget;

		private bool hasUsedWp;


		private List<TaskNode> tasks = new List<TaskNode>();
		private DateTime nextBotAction = DateTime.Now;

		private int numRows, numCols;
		private byte[,] tiles;
		
		internal Vector2 GetMousePosition()
		{
			return new Vector2(GameController.IngameState.MousePosX, GameController.IngameState.MousePosY);
		}

		public AlphaCore()
		{
			Name = "Alpha";
			
		}

		public override bool Initialise()
		{
			Input.RegisterKey(Settings.MovementKey.Value);

			Input.RegisterKey(Settings.ToggleFollower.Value);
			Settings.ToggleFollower.OnValueChanged += () => { Input.RegisterKey(Settings.ToggleFollower.Value); };
			instance = this;

			return base.Initialise();
		}


		/// <summary>
		/// Clears all pathfinding values. Used on area transitions primarily.
		/// </summary>
		private void ResetPathing()
		{
			tasks = new List<TaskNode>();
			followTarget = null;
			lastTargetPosition = Vector3.Zero;
			lastPlayerPosition = Vector3.Zero;
			areaTransitions = new Dictionary<uint, Entity>();
			hasUsedWp = false;
		}

		public override void AreaChange(AreaInstance area)
		{
			ResetPathing();

			//Load initial transitions!

			foreach (var transition in GameController.EntityListWrapper.Entities.Where(I => I.Type == ExileCore.Shared.Enums.EntityType.AreaTransition ||
			 I.Type == ExileCore.Shared.Enums.EntityType.Portal ||
			 I.Type == ExileCore.Shared.Enums.EntityType.TownPortal).ToList())
			{
				if(!areaTransitions.ContainsKey(transition.Id))
					areaTransitions.Add(transition.Id, transition);
			}


			var terrain = GameController.IngameState.Data.Terrain;
			var terrainBytes = GameController.Memory.ReadBytes(terrain.LayerMelee.First, terrain.LayerMelee.Size);
			numCols = (int)(terrain.NumCols - 1) * 23;
			numRows = (int)(terrain.NumRows - 1) * 23;
			if ((numCols & 1) > 0)
				numCols++;

			tiles = new byte[numCols, numRows];
			int dataIndex = 0;
			for (int y = 0; y < numRows; y++)
			{
				for (int x = 0; x < numCols; x += 2)
				{
					var b = terrainBytes[dataIndex + (x >> 1)];
					tiles[x, y] = (byte)((b & 0xf) > 0 ? 1 : 255);
					tiles[x+1, y] = (byte)((b >> 4) > 0 ? 1 : 255);
				}
				dataIndex += terrain.BytesPerRow;
			}

			terrainBytes = GameController.Memory.ReadBytes(terrain.LayerRanged.First, terrain.LayerRanged.Size);
			numCols = (int)(terrain.NumCols - 1) * 23;
			numRows = (int)(terrain.NumRows - 1) * 23;
			if ((numCols & 1) > 0)
				numCols++;
			dataIndex = 0;
			for (int y = 0; y < numRows; y++)
			{
				for (int x = 0; x < numCols; x += 2)
				{
					var b = terrainBytes[dataIndex + (x >> 1)];

					var current = tiles[x, y];
					if(current == 255)
						tiles[x, y] = (byte)((b & 0xf) > 3 ? 2 : 255);
					current = tiles[x+1, y];
					if (current == 255)
						tiles[x + 1, y] = (byte)((b >> 4) > 3 ? 2 : 255);
				}
				dataIndex += terrain.BytesPerRow;
			}


			GeneratePng();
		}

		public void GeneratePng()
		{
			using (var img = new Bitmap(numCols,numRows))
			{
				for (int x = 0; x < numCols; x++)
					for (int y = 0; y < numRows; y++)
					{
						try
						{
							var color = System.Drawing.Color.Black;
							switch (tiles[x, y])
							{
								case 1:
									color = System.Drawing.Color.White;
									break;
								case 2:
									color = System.Drawing.Color.Gray;
									break;
								case 255:
									color = System.Drawing.Color.Black;
									break;
							}
							img.SetPixel(x, y, color);
						}
						catch (Exception e)
						{
							Console.WriteLine(e);
						}
					}
				img.Save("output.png");
			}
		}


		private void MouseoverItem(Entity item)
		{
			var uiLoot = GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
			if (uiLoot != null)
			{
				var clickPos = uiLoot.Label.GetClientRect().Center;
				Mouse.SetCursorPos(new Vector2(
					clickPos.X + random.Next(-15, 15),
					clickPos.Y + random.Next(-10, 10)));
				Thread.Sleep(30 + random.Next(Settings.BotInputFrequency));				
			}
		}

		public override Job Tick()
		{

			//Dont run logic if we're dead!
			if (!GameController.Player.IsAlive)
				return null;

			if (Settings.ToggleFollower.PressedOnce())
			{
				Settings.IsFollowEnabled.SetValueNoEvent(!Settings.IsFollowEnabled.Value);
				tasks = new List<TaskNode>();				
			}

			if (!Settings.IsFollowEnabled.Value)
				return null;


			//Cache the current follow target (if present)
			followTarget = GetFollowingTarget();
			if (followTarget != null)
			{
				var distanceFromFollower = Vector3.Distance(GameController.Player.Pos, followTarget.Pos);
				//We are NOT within clear path distance range of leader. Logic can continue
				if (distanceFromFollower >= Settings.ClearPathDistance.Value)
				{
					//Leader moved VERY far in one frame. Check for transition to use to follow them.
					var distanceMoved = Vector3.Distance(lastTargetPosition, followTarget.Pos);
					if (lastTargetPosition != Vector3.Zero && distanceMoved > Settings.ClearPathDistance.Value)
					{
						var transition = areaTransitions.Values.OrderBy(I => Vector3.Distance(lastTargetPosition, I.Pos)).FirstOrDefault();
						if (transition != null && Vector3.Distance(lastTargetPosition, transition.Pos) < Settings.ClearPathDistance.Value)
							tasks.Add(new TaskNode(transition.Pos, 200, TaskNodeType.Transition));
					}
					//We have no path, set us to go to leader pos.
					else if (tasks.Count == 0)
						tasks.Add(new TaskNode(followTarget.Pos, Settings.PathfindingNodeDistance));
					//We have a path. Check if the last task is far enough away from current one to add a new task node.
					else
					{
						var distanceFromLastTask = Vector3.Distance(tasks.Last().WorldPosition, followTarget.Pos);
						if (distanceFromLastTask >= Settings.PathfindingNodeDistance)
							tasks.Add(new TaskNode(followTarget.Pos, Settings.PathfindingNodeDistance));
					}
				}
				else
				{
					//Clear all tasks except for looting/claim portal (as those only get done when we're within range of leader. 
					if (tasks.Count > 0)
					{
						for (var i = tasks.Count - 1; i >= 0; i--)
							if (tasks[i].Type == TaskNodeType.Movement || tasks[i].Type == TaskNodeType.Transition)
								tasks.RemoveAt(i);
					}
					else if (Settings.IsCloseFollowEnabled.Value)
					{
						//Close follow logic. We have no current tasks. Check if we should move towards leader
						if (distanceFromFollower >= Settings.PathfindingNodeDistance.Value)
							tasks.Add(new TaskNode(followTarget.Pos, Settings.PathfindingNodeDistance));
					}

					//Check if we should add quest loot logic. We're close to leader already
					var questLoot = GetLootableQuestItem();
					if (questLoot != null &&
						Vector3.Distance(GameController.Player.Pos, questLoot.Pos) < Settings.ClearPathDistance.Value &&
						tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
						tasks.Add(new TaskNode(questLoot.Pos, Settings.ClearPathDistance, TaskNodeType.Loot));

					else if (!hasUsedWp && Settings.TakeWaypoints)
					{
						//Check if there's a waypoint nearby
						var waypoint = GameController.EntityListWrapper.Entities.SingleOrDefault(I => I.Type == ExileCore.Shared.Enums.EntityType.Waypoint &&
							Vector3.Distance(GameController.Player.Pos, I.Pos) < Settings.ClearPathDistance);

						if (waypoint != null)
						{
							hasUsedWp = true;
							tasks.Add(new TaskNode(waypoint.Pos, Settings.ClearPathDistance, TaskNodeType.ClaimWaypoint));
						}

					}

				}
				lastTargetPosition = followTarget.Pos;
			}
			//Leader is null but we have tracked them this map.
			//Try using transition to follow them to their map
			else if (tasks.Count == 0 &&
				lastTargetPosition != Vector3.Zero)
			{

				var transOptions = areaTransitions.Values.
					Where(I => Vector3.Distance(lastTargetPosition, I.Pos) < Settings.ClearPathDistance).
					OrderBy(I => Vector3.Distance(lastTargetPosition, I.Pos)).ToArray();
				if (transOptions.Length > 0)
					tasks.Add(new TaskNode(transOptions[random.Next(transOptions.Length)].Pos, Settings.PathfindingNodeDistance.Value, TaskNodeType.Transition));
			}


			//We have our tasks, now we need to perform in game logic with them.
			if (DateTime.Now > nextBotAction && tasks.Count > 0)
			{
				var currentTask = tasks.First();
				var taskDistance = Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition);
				var playerDistanceMoved = Vector3.Distance(GameController.Player.Pos, lastPlayerPosition);

				//We are using a same map transition and have moved significnatly since last tick. Mark the transition task as done.
				if (currentTask.Type == TaskNodeType.Transition && 
					playerDistanceMoved >= Settings.ClearPathDistance.Value)
				{
					tasks.RemoveAt(0);
					if (tasks.Count > 0)
						currentTask = tasks.First();
					else
					{
						lastPlayerPosition = GameController.Player.Pos;
						return null;
					}
				}

				switch (currentTask.Type)
				{
					case TaskNodeType.Movement:
						nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));

						if (Settings.IsDashEnabled && CheckDashTerrain(currentTask.WorldPosition.WorldToGrid()))
							return null;

						Mouse.SetCursorPosHuman2(WorldToValidScreenPosition(currentTask.WorldPosition));
						Thread.Sleep(random.Next(25) + 30);
						Input.KeyDown(Settings.MovementKey);
						Thread.Sleep(random.Next(25) + 30);
						Input.KeyUp(Settings.MovementKey);

						//Within bounding range. Task is complete
						//Note: Was getting stuck on close objects... testing hacky fix.
						if (taskDistance <= Settings.PathfindingNodeDistance.Value * 1.5)
							tasks.RemoveAt(0);
						break;
					case TaskNodeType.Loot:
						{
							nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));
							currentTask.AttemptCount++;
							var questLoot = GetLootableQuestItem();
							if (questLoot == null
								|| currentTask.AttemptCount > 2
								|| Vector3.Distance(GameController.Player.Pos, questLoot.Pos) >= Settings.ClearPathDistance.Value)
								tasks.RemoveAt(0);

							Input.KeyUp(Settings.MovementKey);
							Thread.Sleep(Settings.BotInputFrequency);
							//Pause for long enough for movement to hopefully be finished.
							if (questLoot != null)
							{
								var targetInfo = questLoot.GetComponent<Targetable>();
								switch (targetInfo.isTargeted)
								{
									case false:
										MouseoverItem(questLoot);
										break;
									case true:
										Thread.Sleep(25);
										Mouse.LeftMouseDown();
										Thread.Sleep(25 + random.Next(Settings.BotInputFrequency));
										Mouse.LeftMouseUp();
										nextBotAction = DateTime.Now.AddSeconds(1);
										break;
								}
							}

							break;
						}
					case TaskNodeType.Transition:
						{
							nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency * 2 + random.Next(Settings.BotInputFrequency));
							var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition);							
							if (taskDistance <= Settings.ClearPathDistance.Value)
							{
								//Click the transition
								Input.KeyUp(Settings.MovementKey);
								Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
								nextBotAction = DateTime.Now.AddSeconds(1);
							}
							else
							{
								//Walk towards the transition
								Mouse.SetCursorPosHuman2(screenPos);
								Thread.Sleep(random.Next(25) + 30);
								Input.KeyDown(Settings.MovementKey);
								Thread.Sleep(random.Next(25) + 30);
								Input.KeyUp(Settings.MovementKey);
							}
							currentTask.AttemptCount++;
							if (currentTask.AttemptCount > 3)
								tasks.RemoveAt(0);
							break;
						}

					case TaskNodeType.ClaimWaypoint:
						{
							if (Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition) > 150)
							{
								var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition);
								Input.KeyUp(Settings.MovementKey);
								Thread.Sleep(Settings.BotInputFrequency);
								Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
								nextBotAction = DateTime.Now.AddSeconds(1);
							}
							currentTask.AttemptCount++;
							if (currentTask.AttemptCount > 3)
								tasks.RemoveAt(0);
							break;
						}
				}
			}
			lastPlayerPosition = GameController.Player.Pos;
			return null;
		}

		private bool CheckDashTerrain(Vector2 targetPosition)
		{		

			//TODO: Completely re-write this garbage. 
			//It's not taking into account a lot of stuff, horribly inefficient and just not the right way to do this.
			//Calculate the straight path from us to the target (this would be waypoints normally)
			var dir = targetPosition - GameController.Player.GridPos;
			dir.Normalize();

			var distanceBeforeWall = 0;
			var distanceInWall = 0;

			var shouldDash = false;
			var points = new List<System.Drawing.Point>();
			for (var i = 0; i < 500; i++)
			{
				var v2Point = GameController.Player.GridPos + i * dir;
				var point = new System.Drawing.Point((int)(GameController.Player.GridPos.X + i * dir.X),
					(int)(GameController.Player.GridPos.Y + i * dir.Y));

				if (points.Contains(point))
					continue;
				if (Vector2.Distance(v2Point,targetPosition) < 2)
					break;

				points.Add(point);
				var tile = tiles[point.X, point.Y];


				//Invalid tile: Block dash
				if (tile == 255)
				{
					shouldDash = false;
					break;
				}
				else if (tile == 2)
				{
					if (shouldDash)
						distanceInWall++;
					shouldDash = true;
				}
				else if (!shouldDash)
				{
					distanceBeforeWall++;
					if (distanceBeforeWall > 10)					
						break;					
				}
			}

			if (distanceBeforeWall > 10 || distanceInWall < 5)
				shouldDash = false;

			if (shouldDash)
			{
				nextBotAction = DateTime.Now.AddMilliseconds(500 + random.Next(Settings.BotInputFrequency));
				Mouse.SetCursorPos(WorldToValidScreenPosition(targetPosition.GridToWorld(followTarget == null ? GameController.Player.Pos.Z : followTarget.Pos.Z)));
				Thread.Sleep(50 + random.Next(Settings.BotInputFrequency));
				Input.KeyDown(Settings.DashKey);
				Thread.Sleep(15 + random.Next(Settings.BotInputFrequency));
				Input.KeyUp(Settings.DashKey);
				return true;
			}

			return false;
		}

		private Entity GetFollowingTarget()
		{
			var leaderName = Settings.LeaderName.Value.ToLower();
			try
			{
				return GameController.Entities
					.Where(x => x.Type == ExileCore.Shared.Enums.EntityType.Player)
					.FirstOrDefault(x => x.GetComponent<Player>().PlayerName.ToLower() == leaderName);
			}
			// Sometimes we can get "Collection was modified; enumeration operation may not execute" exception
			catch
			{
				return null;
			}
		}

		private Entity GetLootableQuestItem()
		{
			try
			{
				return GameController.EntityListWrapper.Entities
					.Where(e => e.Type == ExileCore.Shared.Enums.EntityType.WorldItem)
					.Where(e => e.IsTargetable)
					.Where(e => e.GetComponent<WorldItem>() != null)
					.FirstOrDefault(e =>
					{
						Entity itemEntity = e.GetComponent<WorldItem>().ItemEntity;
						return GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName ==
								"QuestItem";
					});
			}
			catch
			{
				return null;
			}
		}
		public override void EntityAdded(Entity entity)
		{
			if (!string.IsNullOrEmpty(entity.RenderName))
				switch (entity.Type)
				{
					//TODO: Handle doors and similar obstructions to movement/pathfinding

					//TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

					//Handle clickable teleporters
					case ExileCore.Shared.Enums.EntityType.AreaTransition:
					case ExileCore.Shared.Enums.EntityType.Portal:
					case ExileCore.Shared.Enums.EntityType.TownPortal:
						if (!areaTransitions.ContainsKey(entity.Id))
							areaTransitions.Add(entity.Id, entity);
						break;
				}
			base.EntityAdded(entity);
		}

		public override void EntityRemoved(Entity entity)
		{
			switch (entity.Type)
			{
				//TODO: Handle doors and similar obstructions to movement/pathfinding

				//TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

				//Handle clickable teleporters
				case ExileCore.Shared.Enums.EntityType.AreaTransition:
				case ExileCore.Shared.Enums.EntityType.Portal:
				case ExileCore.Shared.Enums.EntityType.TownPortal:
					if (areaTransitions.ContainsKey(entity.Id))
						areaTransitions.Remove(entity.Id);
					break;
			}
			base.EntityRemoved(entity);
		}


		public override void Render()
		{
			if (tasks != null && tasks.Count > 1)
				for (var i = 1; i < tasks.Count; i++)
				{
					var start = WorldToValidScreenPosition(tasks[i - 1].WorldPosition);
					var end = WorldToValidScreenPosition(tasks[i].WorldPosition);
					Graphics.DrawLine(start, end, 2, SharpDX.Color.Pink);
				}
			var dist = tasks?.Count > 0 ? Vector3.Distance(GameController.Player.Pos, tasks.First().WorldPosition): 0;
			var targetDist = Vector3.Distance(GameController.Player.Pos, lastTargetPosition).ToString(CultureInfo.InvariantCulture);
			Graphics.DrawText($"Follow Enabled: {Settings.IsFollowEnabled.Value}", new Vector2(500, 120));
			Graphics.DrawText($"Task Count: {tasks?.Count} Next WP Distance: {dist} Target Distance: {targetDist}", new Vector2(500, 140));
			var counter = 0;
			foreach (var transition in areaTransitions)
			{
				counter++;
				Graphics.DrawText($"{transition.Key} at { transition.Value.Pos.X} { transition.Value.Pos.Y}", new Vector2(100, 120 + counter * 20));
			}
		}


		private Vector2 WorldToValidScreenPosition(Vector3 worldPos)
		{
			var windowRect = GameController.Window.GetWindowRectangle();
			var screenPos = Camera.WorldToScreen(worldPos);
			var result = screenPos + windowRect.Location;

			var edgeBounds = 50;
			if (!windowRect.Intersects(new SharpDX.RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
			{
				//Adjust for offscreen entity. Need to clamp the screen position using the game window info. 
				if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
				if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
				if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
				if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
			}
			return result;
		}
	}
}
