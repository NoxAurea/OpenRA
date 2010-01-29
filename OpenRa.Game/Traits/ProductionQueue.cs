﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IjwFramework.Collections;

namespace OpenRa.Traits
{
	class ProductionQueueInfo : ITraitInfo
	{
		public object Create(Actor self) { return new ProductionQueue(self); }
	}

	class ProductionQueue : IResolveOrder, ITick
	{
		Actor self;

		public ProductionQueue( Actor self )
		{
			this.self = self;
		}

		public void Tick( Actor self )
		{
			foreach( var p in production )
				if( p.Value.Count > 0 )
					(p.Value)[0].Tick( self.Owner );
		}

		public void ResolveOrder( Actor self, Order order )
		{
			switch( order.OrderString )
			{
			case "StartProduction":
				{
					var unit = Rules.Info[ order.TargetString ];
					var ui = unit.Traits.Get<BuildableInfo>();
					var time = ui.Cost
						* Rules.General.BuildSpeed						/* todo: country-specific build speed bonus */
						 * ( 25 * 60 ) /* frames per min */				/* todo: build acceleration, if we do that */
						 / 1000;

					time = .08f * time;						/* temporary hax so we can build stuff fast for test */

					if( !Rules.TechTree.BuildableItems( order.Player, unit.Category ).Contains( order.TargetString ) )
						return;	/* you can't build that!! */

					bool hasPlayedSound = false;

					BeginProduction( unit.Category,
						new ProductionItem( order.TargetString, (int)time, ui.Cost,
							() => self.World.AddFrameEndTask(
								_ =>
								{
									var isBuilding = unit.Traits.Contains<BuildingInfo>();
									if( !hasPlayedSound )
									{
										Sound.PlayToPlayer( order.Player, isBuilding ? "conscmp1.aud" : "unitrdy1.aud" );
										hasPlayedSound = true;
									}
									if( !isBuilding )
										BuildUnit( order.TargetString );
								} ) ) );
					break;
				}
			case "PauseProduction":
				{
					var producing = CurrentItem( Rules.Info[ order.TargetString ].Category );
					if( producing != null && producing.Item == order.TargetString )
						producing.Paused = ( order.TargetLocation.X != 0 );
					break;
				}
			case "CancelProduction":
				{
					CancelProduction(order.TargetString);
					break;
				}
			}
		}

		// Key: Production category.
		// TODO: sync this
		readonly Cache<string, List<ProductionItem>> production 
			= new Cache<string, List<ProductionItem>>( _ => new List<ProductionItem>() );

		public ProductionItem CurrentItem(string category)
		{
			return production[category].ElementAtOrDefault(0);
		}

		public IEnumerable<ProductionItem> AllItems(string category)
		{
			return production[category];
		}

		public void CancelProduction( string itemName )
		{
			var category = Rules.Info[itemName].Category;
			var queue = production[ category ];
			if (queue.Count == 0) return;

			var lastIndex = queue.FindLastIndex( a => a.Item == itemName );
			if (lastIndex > 0)
			{
				queue.RemoveAt(lastIndex);
			}
			else
			{
				var item = queue[0];
				self.Owner.GiveCash(item.TotalCost - item.RemainingCost); // refund what's been paid so far.
				FinishProduction(category);
			}
		}

		public void FinishProduction( string category )
		{
			var queue = production[category];
			if (queue.Count == 0) return;
			queue.RemoveAt(0);
		}

		public void BeginProduction( string group, ProductionItem item )
		{
			production[group].Add(item);
		}

		public void BuildUnit( string name )
		{
			var newUnitType = Rules.Info[ name ];
			var producerTypes = Rules.TechTree.UnitBuiltAt( newUnitType );
			Actor producer = null;
			
			// Prioritise primary structure in build order
			var primaryProducers = self.World.Actors
				.Where(x => x.traits.Contains<Production>()
					&& producerTypes.Contains(x.Info)
					&& x.Owner == self.Owner
					&& x.traits.Get<Production>().IsPrimary == true);
			
			foreach (var p in primaryProducers)
			{
				// Ignore buildings that are disabled
				if (p.traits.Contains<Building>() && p.traits.Get<Building>().Disabled)
					continue;
				producer = p;
				break;
			}
			
			// TODO: Be smart about disabled buildings. Units in progress should be paused(?)
			// Ignore this for now
			
			// Pick the first available producer
			if (producer == null)
			{
				producer = self.World.Actors
					.Where( x => producerTypes.Contains( x.Info ) && x.Owner == self.Owner )
					.FirstOrDefault();
			}
			
			// Something went wrong somewhere...
			if( producer == null )
			{
				CancelProduction( newUnitType.Category );
				return;
			}

			if( producer.traits.WithInterface<IProducer>().Any( p => p.Produce( producer, newUnitType ) ) )
				FinishProduction( newUnitType.Category );
		}
	}

	class ProductionItem
	{
		public readonly string Item;

		public readonly int TotalTime;
		public readonly int TotalCost;
		public int RemainingTime { get; private set; }
		public int RemainingCost { get; private set; }

		public bool Paused = false, Done = false;
		public Action OnComplete;

		int slowdown = 0;

		public ProductionItem(string item, int time, int cost, Action onComplete)
		{
			if (time <= 0)
				time = 1;
			Item = item;
			RemainingTime = TotalTime = time;
			RemainingCost = TotalCost = cost;
			OnComplete = onComplete;
		}

		public void Tick(Player player)
		{
			if (Done)
			{
				if (OnComplete != null) OnComplete();
				return;
			}

			if (Paused) return;

			if (player.GetPowerState() != PowerState.Normal)
			{
				if (--slowdown <= 0)
					slowdown = Rules.General.LowPowerSlowdown;
				else
					return;
			}

			var costThisFrame = RemainingCost / RemainingTime;
			if (costThisFrame != 0 && !player.TakeCash(costThisFrame)) return;

			RemainingCost -= costThisFrame;
			RemainingTime -= 1;
			if (RemainingTime > 0) return;

			Done = true;
		}
	}
}
