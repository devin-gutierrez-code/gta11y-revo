# iter70 animal direct-melee setting repair

## Finding

The iter-69 bodyguard log recorded `animal-direct-melee-toggle value=1`, but every
subsequent animal assignment recorded `task=combat-ped`. `animalDirectMelee` was used by
the bodyguard menu and combat paths but was absent from `setupSettings`' canonical ID
list. `getSetting` therefore returned `-1`, while `setSetting` found no row to update and
still allowed the menu to announce success.

## Repair

- Register `animalDirectMelee` beside the other bodyguard settings.
- Give it an explicit off default and an accessible Settings-menu name.
- Advance both diagnostic banners to `iter70-animal-melee-setting`.

## Field verification

Enable **Animal direct melee**, spawn an animal guard, and provoke combat. The next
`gta11y-bodyguards-*.log` must contain the iter-70 banner followed by
`animal-direct-melee-toggle value=1` and `task=direct-melee`. A live target should remain
sticky: recurring `tactics-next` assignments for that guard must stop until its target
dies or disappears.
